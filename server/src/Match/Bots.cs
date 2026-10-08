using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using EpidemicServer.Resolve;
using System.Reflection;

namespace EpidemicServer.Match
{
    /// <summary>
    /// Bot heroes (owner's design, 2026-10-07: Scavenger with bots from the start). The client has no player AI
    /// (Player.IsBot is only a flag), so a bot is a real hero Player on a server-side client slot (1-11), flagged
    /// IsBot, and driven the way a human's input drives a hero:
    /// - movement: Character.InputFlags, which Character.GetMoveDirection turns into a direction relative to the
    ///   client's camera direction (1 = -right, 2 = +right, 4 = forward, 8 = back, right = (cam.Y, -cam.X));
    /// - aim: TargetAimDirection and MousePosition;
    /// - attacks and actions: AbilityBar.SetAbilityPressed(slot, pressed), as the human's controller record does.
    /// The human's client learns about bot clients from a client-info update (reliable kind 0: count, then per
    /// client its index and Client.ClientInfoServerToClientSerialize) and sees their aim through the controller
    /// section of each frame (MatchFrames.WriteControllers).
    /// </summary>
    public sealed class Bot
    {
        public int ClientIndex;
        public object Player, Client;
        public string Character, Team, Name;
        public float[] Goal;              // where to go (null: stand)
        public object Target;             // what to attack (null: nothing)
        public int AttackSlot = BotDriver.PrimarySlot;
        public float NextPath, NextAttack;
        public float[] Waypoint;
        public bool PressedLast;
        public float[] LastPos, PathTo;
        public float TargetSince, TargetHp, DetourUntil, ProgressAt, BestDist;
        public float RespawnedAt = -100f;
        public float[] ProgressTo;
        public int DetourSide = 1;
        public readonly Dictionary<object, float> Ignore = new Dictionary<object, float>();
        public float LastMoved;
        public List<float[]> Path;
    }

    public static class BotDriver
    {
        private const BindingFlags All = GameRuntime.All;
        /// <summary>The weapon's primary attack slot on a hero's ability bar (probe: slot 0 deals the melee hit).</summary>
        public const int PrimarySlot = 0;
        /// <summary>Barricade pieces are wide and solid: hit them from further out (ours).</summary>
        public const float BarricadeRange = 55f;
        public const float AttackRange = 14f, Arrive = 6f, RepathDistance = 40f, StuckSeconds = 3f, RepathSeconds = 12f;
        /// <summary>Pathfinder iteration budget for bots (ours; zombies use 2000, too few across a Scavenger map).</summary>
        public const int PathIterations = 30000;
        /// <summary>The match's waypoint network (Scavenger); trips longer than ShortTrip use it.</summary>
        public static RouteGraph Graph;
        public const float ShortTrip = 250f;
        public const float DetourSeconds = 1.5f, UnstickSeconds = 20f;
        /// <summary>
        /// Shorter unstick wait just after a respawn: some maps' respawn points sit in pockets the navmesh can't
        /// route out of (offline, Resort: most unsticks were bots waiting out 20 s at their team's respawn point).
        /// </summary>
        public const float RespawnUnstickSeconds = 10f, RespawnWindow = 30f;
        public static Action<string> Log;

        public static Bot Create(GameRuntime g, int clientIndex, string character, string team, string name, SpawnPoint spawn, int level, Weapon melee, Weapon ranged)
        {
            object info = g.BuildClientInfoData(MatchHost.DefaultCameraX, MatchHost.DefaultCameraY, name, team, level, melee, ranged);
            // Bots get made-up user ids (not Steam ids): 0x7FFF0000 + slot.
            object player = g.PreparePlayer(clientIndex, character, spawn, info, 0, 0x7FFF0000UL + (ulong)clientIndex, team, level, true);
            g.BotClients.Add(clientIndex);
            return new Bot { ClientIndex = clientIndex, Player = player, Client = g.GetClient(clientIndex), Character = character, Team = team, Name = name };
        }

        /// <summary>The client-info update that tells the human's client who the bots are (reliable kind 0).</summary>
        public static void AnnounceBots(GameRuntime g)
        {
            if (g.BotClients.Count == 0) return;
            if (ServerHooks.QueueReliable == null) return;
            GameBuffer m = GameBuffer.Create();
            m.Write((byte)0);
            m.Write((byte)g.BotClients.Count);
            foreach (int i in g.BotClients)
            {
                m.Write((byte)i);
                object c = g.GetClient(i);
                c.GetType().GetMethod("ClientInfoServerToClientSerialize", All).Invoke(c, new[] { m.Buffer, (object)true });
            }
            ServerHooks.QueueReliable(m.Buffer);
        }

        public static bool IsDead(object o) { return (bool)o.GetType().GetProperty("IsDead", All).GetValue(o, null); }

        public static bool Gone(object o)
        {
            return o == null || IsDead(o) || !(bool)o.GetType().GetProperty("IsActive", All).GetValue(o, null);
        }

        /// <summary>One tick of a bot's body: walk toward Goal (navmesh waypoints), face and hit Target in range.</summary>
        public static void Drive(GameRuntime g, Bot b, float time)
        {
            object p = b.Player;
            Type t = p.GetType();
            if (IsDead(p)) { SetInput(p, 0); Press(b, false); b.ProgressAt = time; b.ProgressTo = null; return; }
            float[] here = ServerHooks.Position(p);
            float[] to = b.Target != null && !Gone(b.Target) ? ServerHooks.Position(b.Target) : b.Goal;
            float dist = to == null ? 0f : Dist(here, to);
            bool attacking = b.Target != null && !Gone(b.Target) && dist <= (b.Target.GetType().Name == R.Short("Barricade") ? BarricadeRange : AttackRange);
            // Last resort (ours, a stand-in): a bot with somewhere to go that has not moved 30 units in
            // UnstickSeconds is moved to the spawn spot nearest its destination that it can reach from there
            // (Scavenger maps have pockets the navmesh search can't route out of).
            if (to != null && !attacking && dist > Arrive && Graph != null)
            {
                // Progress = the bot got 30 units closer to its destination than its best since the destination
                // last changed. A new destination (more than 60 units from the old) resets the best distance but
                // not the clock, so a bot whose goal flips between two rooms while pacing against a wall is still
                // caught; arriving or fighting counts as progress (below). Body movement alone was fooled by
                // pacing (Team3 on Resort, 2026-10-07 playtest: four bots at one spot for the whole match).
                if (b.ProgressTo == null || Dist(b.ProgressTo, to) > 60f) { b.ProgressTo = to; b.BestDist = dist; }
                else if (dist <= b.BestDist - 30f) { b.BestDist = dist; b.ProgressAt = time; }
                else if (time - b.ProgressAt >= (time - b.RespawnedAt < RespawnWindow ? RespawnUnstickSeconds : UnstickSeconds))
                {
                    float[] spot = Graph.Nodes.Where(n => Dist(n, to) < dist - 60f).OrderBy(n => Dist(n, to)).FirstOrDefault(n => RouteGraph.Reachable(n, to) || Dist(n, to) < 40f)
                                   ?? Graph.Nodes.OrderBy(n => Dist(n, to)).FirstOrDefault();
                    if (spot != null)
                    {
                        object v = g.Vector2(spot[0], spot[1]);
                        g.Game("ConductorGameLogic.Entities.Entity").GetMethod("Teleport", All).Invoke(p, new object[] { v, true });
                        if (Log != null) Log("scavenger: bot " + b.Name + " stuck at (" + here[0].ToString("0") + ", " + here[1].ToString("0") + ") for " + (time - b.ProgressAt).ToString("0") + " s; moved to (" + spot[0].ToString("0") + ", " + spot[1].ToString("0") + ")");
                        b.Path = null; b.ProgressAt = time; b.ProgressTo = to; b.BestDist = Dist(spot, to);
                        return;
                    }
                }
            }
            else b.ProgressAt = time;   // arrived, fighting or idle: not stuck
            if (to != null && !attacking && dist > Arrive)
            {
                // Follow a cached navmesh path (one search per destination; the search is too slow to repeat
                // for 11 bots every half second on a Scavenger map). New search when the destination moved more
                // than RepathDistance, when stuck for StuckSeconds, or after RepathSeconds.
                if (b.LastPos == null || Dist(b.LastPos, here) >= 3f) { b.LastPos = here; b.LastMoved = time; }
                bool stuck = time - b.LastMoved >= StuckSeconds;
                if (b.Path == null || b.PathTo == null || Dist(b.PathTo, to) > RepathDistance || stuck || time >= b.NextPath)
                {
                    b.NextPath = time + RepathSeconds;
                    b.PathTo = to;
                    b.LastMoved = time;
                    // Stuck with a "clear" straight line (the collision check misses some walls): ask the navmesh anyway.
                    // Long trips go over the waypoint network; short ones (and fallbacks) use the navmesh.
                    b.Path = null;
                    if (Graph != null && Dist(here, to) > ShortTrip)
                        try { b.Path = Graph.Route(here, to); } catch (Exception) { b.Path = null; }
                    if (b.Path == null)
                        try { b.Path = ServerHooks.Path(here, to, PathIterations, stuck); } catch (Exception) { b.Path = null; }
                    // No path: either the destination is off the navmesh (a storage room's flag stands on a prop),
                    // or it is too far for one search (probe: ~300 units route, ~900 don't). Try spots on rings
                    // around it, then the same for points part of the way there; the bot re-plans on arrival (ours).
                    foreach (float frac in new[] { 1f, 0.6f, 0.35f, 0.2f })
                    {
                        if (b.Path != null) break;
                        float[] mid = { here[0] + (to[0] - here[0]) * frac, here[1] + (to[1] - here[1]) * frac };
                        for (int ring = 0; b.Path == null && ring <= 3; ring++)
                            for (int k = 0; k < (ring == 0 ? 1 : 8) && b.Path == null; k++)
                            {
                                double ang = k * Math.PI / 4;
                                float[] near = { mid[0] + (float)Math.Cos(ang) * 12f * ring, mid[1] + (float)Math.Sin(ang) * 12f * ring };
                                if (frac == 1f && ring == 0) continue;   // the destination itself was tried above
                                try
                                {
                                    b.Path = ServerHooks.Path(here, near, PathIterations, true);
                                    if (b.Path != null) { b.Path.Add(near); if (frac < 1f) b.NextPath = time + 4f; }
                                }
                                catch (Exception) { b.Path = null; }
                            }
                    }
                    if (b.Path == null) b.Path = new List<float[]>();
                }
                while (b.Path.Count > 0 && Dist(b.Path[0], here) < 12f) b.Path.RemoveAt(0);
                float[] step = b.Path.Count > 0 ? b.Path[0] : to;
                b.Waypoint = step;
                float dx = step[0] - here[0], dy = step[1] - here[1];
                // Wall following (ours): the navmesh misses some routes (spawn points and trucks off the mesh),
                // so a bot that has not moved for a second sidesteps at 90 degrees for DetourSeconds, turning to
                // the other side on each new stall, then heads for its waypoint again.
                if (time - b.LastMoved >= 1f && time >= b.DetourUntil)
                {
                    b.DetourUntil = time + DetourSeconds;
                    b.DetourSide = -b.DetourSide;
                    b.LastMoved = time;
                }
                if (time < b.DetourUntil)
                {
                    float rx = -dy * b.DetourSide, ry = dx * b.DetourSide;
                    dx = rx + dx * 0.3f; dy = ry + dy * 0.3f;
                }
                SetInput(p, Flags(g, b, dx, dy));
                Aim(g, p, dx, dy, new[] { here[0] + dx, here[1] + dy });
            }
            else if (attacking && b.Target.GetType().Name == R.Short("Barricade") && dist > 16f)
                SetInput(p, Flags(g, b, to[0] - here[0], to[1] - here[1]));   // lean into a barricade while hitting it
            else SetInput(p, 0);
            if (attacking)
            {
                Aim(g, p, to[0] - here[0], to[1] - here[1], to);
                // Tap the attack: pressed one tick, released the next (a held button only fires once).
                bool press = time >= b.NextAttack && !b.PressedLast;
                if (press) b.NextAttack = time + 0.35f;
                Press(b, press);
            }
            else Press(b, false);
        }

        private static void Press(Bot b, bool pressed)
        {
            if (pressed == b.PressedLast) return;
            b.PressedLast = pressed;
            object bar = b.Player.GetType().GetProperty("AbilityBar", All).GetValue(b.Player, null);
            bar.GetType().GetMethod("SetAbilityPressed", All).Invoke(bar, new object[] { b.AttackSlot, pressed });
        }

        /// <summary>Taps any ability slot (capture 19, deliver 17, steal 28, ...).</summary>
        public static void Tap(Bot b, int slot, bool pressed)
        {
            object bar = b.Player.GetType().GetProperty("AbilityBar", All).GetValue(b.Player, null);
            bar.GetType().GetMethod("SetAbilityPressed", All).Invoke(bar, new object[] { slot, pressed });
        }

        private static void SetInput(object p, int flags)
        {
            PropertyInfo f = p.GetType().GetProperty("InputFlags", All);
            f.SetValue(p, Enum.ToObject(f.PropertyType, flags), null);
        }

        private static void Aim(GameRuntime g, object p, float dx, float dy, float[] at)
        {
            float len = (float)Math.Sqrt(dx * dx + dy * dy);
            if (len < 0.001f) return;
            object dir = g.Vector2(dx / len, dy / len);
            ServerHooks.SetField(p, "TargetAimDirection", dir);
            PropertyInfo mouse = p.GetType().GetProperty("MousePosition", All);
            if (mouse != null && mouse.CanWrite) mouse.SetValue(p, g.Vector2(at[0], at[1]), null);
        }

        /// <summary>The input flags closest to a direction, relative to the bot client's camera direction.</summary>
        private static int Flags(GameRuntime g, Bot b, float dx, float dy)
        {
            float len = (float)Math.Sqrt(dx * dx + dy * dy);
            if (len < 0.001f) return 0;
            dx /= len; dy /= len;
            float cx = MatchHost.DefaultCameraX, cy = MatchHost.DefaultCameraY, cl = (float)Math.Sqrt(cx * cx + cy * cy);
            cx /= cl; cy /= cl;
            float rx = cy, ry = -cx;                 // GetMoveDirection's "right" = (cam.Y, -cam.X)
            float fwd = dx * cx + dy * cy, right = dx * rx + dy * ry;
            int flags = 0;
            if (fwd > 0.38f) flags |= 4; else if (fwd < -0.38f) flags |= 8;
            if (right > 0.38f) flags |= 2; else if (right < -0.38f) flags |= 1;
            return flags;
        }

        public static float Dist(float[] a, float[] b)
        {
            float dx = a[0] - b[0], dy = a[1] - b[1];
            return (float)Math.Sqrt(dx * dx + dy * dy);
        }
    }
}
