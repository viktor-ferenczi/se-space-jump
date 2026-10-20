using System;
using System.Collections.Generic;
using Sandbox;
using Sandbox.Game.Entities;
using Sandbox.Game.GameSystems;
using Sandbox.Engine.Utils;
using Sandbox.Game.World;
using VRage.Game;
using VRage.Game.Entity;
using VRageMath;

using ClientPlugin.Patches;

namespace ClientPlugin.Effects;

/// <summary>
/// Owns the clouds: starts them from the jump drive events, hides grids that jump in next to this client
/// until their cloud has formed, draws everything and the cloud around a third person camera that jumps along.
/// </summary>
public static class JumpEffects
{
    // Grids arriving by a jump this client only learned of from the broadcast, by entity id
    private const double ArrivalWindowSeconds = 20;

    // Grids hidden this long at most, whatever happens to their cloud
    private const double MaxHideSeconds = 6;

    // Grid shown again this long after its arrival cloud started
    private const double ShowAfterCloudSeconds = 0.7;

    private static readonly Dictionary<long, Cloud> Origins = new Dictionary<long, Cloud>();
    private static readonly List<Cloud> Clouds = new List<Cloud>();

    // For the diagnostics log: where and when ships arrived lately (a remote jump's target, or a destination cloud)
    private static readonly List<(Vector3D Target, double At)> RecentArrivals = new List<(Vector3D, double)>();

    // Within this long after an arrival and this far from it, the diagnostics log its markers and clouds
    private const double ArrivalLogSeconds = 6;
    private const double ArrivalLogDistance = 300;

    private static double lastSizeLogAt;

    /// <summary>Any cloud about: the markers have something to hide</summary>
    public static bool Active => Clouds.Count != 0;

    private static readonly Dictionary<long, Arrival> Arrivals = new Dictionary<long, Arrival>();
    private static readonly List<Hidden> HiddenGrids = new List<Hidden>();

    private static MySession session;

    private sealed class Arrival
    {
        public Vector3D Target;
        public double At;
        public Cloud Provisional;
    }

    private sealed class Hidden
    {
        public MyCubeGrid Grid;
        public Cloud Cloud;
        public double At;
    }

    public static double Now => MySandboxGame.TotalGamePlayTimeInMilliseconds / 1000.0;

    private static double MaxDistance => Config.Current.MaxDistanceKm * 1000.0;

    private static Vector3D CameraPosition => MySector.MainCamera?.Position ?? Vector3D.Zero;

    private static bool IsNearCamera(Vector3D position) => Vector3D.Distance(CameraPosition, position) <= MaxDistance;

    #region Jump drive events

    public static void OnCountdown(MyGridJumpDriveSystem system, float timeLeft)
    {
        var grid = system.Grid;
        if (grid == null || grid.MarkedForClose || grid.Physics == null)
            return;

        var now = Now;
        if (Origins.TryGetValue(grid.EntityId, out var cloud) && !cloud.Finished)
        {
            cloud.JumpAt = now + Math.Max(timeLeft, 0f);
            return;
        }

        if (!IsNearCamera(grid.PositionComp.WorldAABB.Center) && !IsNearCamera(system.m_selectedDestination) && !CameraJumpsWith(grid))
            return;

        cloud = Cloud.CreateOrigin(grid, now, now + timeLeft);
        Origins[grid.EntityId] = cloud;
        Clouds.Add(cloud);
        Log.Debug($"Countdown of {grid.DisplayName} ({grid.EntityId}): {timeLeft:0.0} s");
    }

    public static void OnJumped(MyGridJumpDriveSystem system, MatrixD preJumpMatrix, Vector3 velocity)
    {
        var grid = system.Grid;
        if (grid == null)
            return;

        var now = Now;
        if (Origins.TryGetValue(grid.EntityId, out var origin))
        {
            Origins.Remove(grid.EntityId);
            MarkerStates.Clear();
            // The jump is heard there by those staying behind: a camera on the ship is already gone
            origin.Detach(preJumpMatrix, grid.PositionComp.GetPosition(), velocity, now, heard: !CameraJumpsWith(grid));
        }

        if (Arrivals.TryGetValue(grid.EntityId, out var arrival))
        {
            arrival.Provisional?.FadeOut(now);
            Arrivals.Remove(grid.EntityId);
        }

        if (!IsNearCamera(grid.PositionComp.WorldAABB.Center) && !CameraJumpsWith(grid))
            return;

        // Its gate sounds the jump at the exit for everyone near it, the pilots who just arrived in it too
        Clouds.Add(Cloud.CreateDestination(grid, now, origin));
        if (Log.DebugEnabled)
            RecentArrivals.Add((grid.PositionComp.GetPosition(), now));
        Log.Debug($"Jumped: {grid.DisplayName} ({grid.EntityId}) to {grid.PositionComp.GetPosition()}");
    }

    public static void OnAborted(MyGridJumpDriveSystem system)
    {
        var grid = system.Grid;
        if (grid == null || !Origins.TryGetValue(grid.EntityId, out var cloud))
            return;

        Origins.Remove(grid.EntityId);
        cloud.Abort(Now);
        Log.Debug($"Aborted: {grid.DisplayName} ({grid.EntityId})");
    }

    // The HUD message of the camera's own jump (it replaces the vanilla countdown), until this long after the jump
    private const double ExitUntil = 6;

    // The HUD message: the gate manifests until the counter turns to 2 s, then the destination locks,
    // the transit is complete from the jump sound on. Until the jump the seconds left are in brackets, rounded as the
    // vanilla countdown rounds them (10 .. 1), down to 0 at the end; the label follows the number shown.
    private static string JumpMessage(double? t)
    {
        if (t is not double since || since >= ExitUntil)
            return null;
        if (since >= 0)
            return "Transit Complete";

        var seconds = Math.Round(-since);
        return $"{(seconds > LockingFromSeconds ? "Manifesting Gate" : "Locking Destination")} ({seconds:0} s)";
    }

    private const double LockingFromSeconds = 2;

    /// <summary>Seconds since the jump of the camera's grid (negative in the countdown), null when it is not jumping</summary>
    private static double? CameraJumpTime(double now)
    {
        if (CameraGridId() is not long id)
            return null;

        foreach (var cloud in Clouds)
        {
            if (cloud.Finished || !cloud.GroupIds.Contains(id))
                continue;

            double t;
            if (cloud.Kind == CloudKind.Origin && !cloud.Aborted && !cloud.Detached)
                t = now - cloud.JumpAt;
            else if (cloud.Kind == CloudKind.Destination)
                t = now - cloud.CreatedAt;
            else
                continue;

            return t;
        }

        return null;
    }

    /// <summary>A grid this client does not have jumped: if it arrives near us, it is streamed in shortly</summary>
    public static void OnRemoteJump(long entityId, Vector3D target)
    {
        var now = Now;
        var arrival = new Arrival { Target = target, At = now };
        if (IsNearCamera(target))
        {
            arrival.Provisional = Cloud.CreateProvisional(target, entityId, now);
            Clouds.Add(arrival.Provisional);
        }

        Arrivals[entityId] = arrival;
        if (Log.DebugEnabled)
            RecentArrivals.Add((target, now));
        Log.Debug($"Remote jump of {entityId} to {target}");
    }

    private static void OnEntityAdd(MyEntity entity)
    {
        try
        {
            if (entity is not MyCubeGrid grid || !Config.Current.Enabled)
                return;

            var now = Now;
            if (Arrivals.TryGetValue(grid.EntityId, out var arrival))
            {
                Arrivals.Remove(grid.EntityId);
                if (now - arrival.At > ArrivalWindowSeconds)
                    return;

                // Gone before the grid shows (ShowAfterCloudSeconds): it knows nothing of the grid's sealed rooms
                arrival.Provisional?.FadeOut(now);
                var cloud = Cloud.CreateDestination(grid, now, null);
                Clouds.Add(cloud);
                Hide(grid, cloud, now);
                if (arrival.Provisional != null)
                    cloud.TakeOver(arrival.Provisional, now);
                Log.Debug($"Arrived: {grid.DisplayName} ({grid.EntityId}), hidden until its cloud formed");
                return;
            }

            // Subgrids of an arriving grid come with it, near its cloud
            foreach (var hidden in HiddenGrids)
            {
                if (hidden.Cloud.GridId == 0 || now - hidden.At > 2 || hidden.Grid.MarkedForClose)
                    continue;

                var box = hidden.Grid.PositionComp.WorldAABB.GetInflated(50);
                if (box.Contains(grid.PositionComp.GetPosition()) != ContainmentType.Disjoint)
                {
                    hidden.Cloud.GroupIds.Add(grid.EntityId);
                    Hide(grid, hidden.Cloud, now);
                    break;
                }
            }
        }
        catch (Exception e)
        {
            Log.Error(e);
        }
    }

    private static void Hide(MyCubeGrid grid, Cloud cloud, double now)
    {
        if (!Config.Current.HideArrivingGrids)
            return;

        grid.Render.Visible = false;
        HiddenGrids.Add(new Hidden { Grid = grid, Cloud = cloud, At = now });
    }

    #endregion

    #region Update and draw

    /// <summary>Simulation frame, main thread</summary>
    public static void Update()
    {
        if (MySession.Static != session)
        {
            Reset();
            session = MySession.Static;
            if (session != null)
            {
                MyEntities.OnEntityAdd += OnEntityAdd;
                JumpSounds.Apply();
            }
        }

        if (session == null)
            return;

        var now = Now;
        var heard = 0f;
        foreach (var cloud in Clouds)
        {
            cloud.Poll(now);
            cloud.UpdateSound(now, CameraPosition);
            heard = Math.Max(heard, cloud.HeardLevel);
        }

        MusicDuck.Update(heard, double.IsNaN(lastUpdateAt) ? 0f : (float)Math.Max(0, now - lastUpdateAt));
        lastUpdateAt = now;

        JumpSounds.Update();
        var jumpTime = CameraJumpTime(now);
        NotificationPatches.ShowJumpMessage(JumpMessage(jumpTime), jumpTime);
        var markersOut = CameraMarkersHidden(now) >= 0.5f;
        if (markersOut != cameraMarkersOut)
        {
            cameraMarkersOut = markersOut;
            Log.Debug($"Transit markers {(markersOut ? "out" : "in")} at t{jumpTime:+0.00;-0.00}");
        }

        LogArrivalCloudSizes(now);

        for (var i = HiddenGrids.Count - 1; i >= 0; i--)
        {
            var hidden = HiddenGrids[i];
            var formed = hidden.Cloud.Started && now - hidden.Cloud.FormingSince >= ShowAfterCloudSeconds;
            if (formed || hidden.Cloud.Finished || now - hidden.At > MaxHideSeconds || hidden.Grid.MarkedForClose)
            {
                Show(hidden.Grid);
                HiddenGrids.RemoveAt(i);
            }
        }

        foreach (var cloud in Clouds)
        {
            if (!cloud.Finished)
                continue;

            cloud.StopSound();
            Log.Debug($"{cloud.Kind} cloud of {cloud.Grid?.DisplayName ?? "(not replicated yet)"} finished, {cloud.ClampedByShip} puff draws cut down at the jumping grids' sealed air, {cloud.ClampedByObstacle} at other grids' sealed air; discharges heard so far: {JumpSounds.StrikesPlayed} strikes, {JumpSounds.SnapsPlayed} snaps, {JumpSounds.FlashesPlayed} flashes");
        }

        Clouds.RemoveAll(cloud => cloud.Finished);

        List<long> expired = null;
        foreach (var pair in Arrivals)
        {
            if (now - pair.Value.At > ArrivalWindowSeconds)
                (expired ??= new List<long>()).Add(pair.Key);
        }

        if (expired != null)
        {
            foreach (var entityId in expired)
                Arrivals.Remove(entityId);
        }

        foreach (var pair in new List<KeyValuePair<long, Cloud>>(Origins))
        {
            if (pair.Value.Finished)
                Origins.Remove(pair.Key);
        }
    }

    private static void Show(MyCubeGrid grid)
    {
        if (!grid.MarkedForClose)
            grid.Render.Visible = true;
    }

    /// <summary>
    /// Whether the HUD marker of an entity (a block, a grid, a seated character, a GPS on one) is drawn this frame: the
    /// markers of a jumping ship flicker out and back in with its clouds, see <see cref="Cloud.MarkersHidden"/>
    /// </summary>
    public static bool MarkerVisible(MyEntity entity)
    {
        // The game never draws the local character's own marker
        if (Clouds.Count == 0 || entity == null || entity == MySession.Static?.LocalCharacter)
            return true;

        // A remote broadcaster's proxy stands for its grid (an antenna, a beacon) or its character (a pilot's suit)
        var key = entity.GetTopMostParent() is MyCubeGrid grid ? grid.EntityId
            : entity is MyProxyAntenna proxy ? proxy.Info.EntityId
            : entity.EntityId;
        return MarkerVisible(key, entity.PositionComp.GetPosition(), entity.EntityId, entity.GetType().Name);
    }

    /// <summary>
    /// A marker of a place: an unknown signal, a point of interest, ore, or the marker of a remote broadcaster (the
    /// HUD knows only the entity id of its block or character, see <see cref="MarkerPatches"/>)
    /// </summary>
    public static bool MarkerVisible(Vector3D position, long? entityId = null)
    {
        if (Clouds.Count == 0)
            return true;

        if (entityId is long id)
            return MarkerVisible(id, position, id, "broadcast");

        var now = Now;
        var hidden = CameraMarkersHidden(now);
        foreach (var cloud in Clouds)
            hidden = Math.Max(hidden, cloud.MarkersHiddenNear(position, now));
        return Flickers(hidden, position.GetHashCode(), now);
    }

    // After its ship's jump, a marker is shown only once its position is at the arrival: a remote client learns of the
    // new position a moment later, and a marker drawn at the old one would show in the departure cloud
    private static bool MarkerVisible(long key, Vector3D position, long logId, string what)
    {
        var now = Now;
        var hidden = CameraMarkersHidden(now);
        Cloud leftBehind = null;
        foreach (var cloud in Clouds)
        {
            hidden = Math.Max(hidden, Math.Max(cloud.MarkersHidden(key, now), cloud.MarkersHiddenNear(position, now)));
            if (cloud.MarkerLeftBehind(key, position))
                leftBehind = cloud;
        }

        var visible = leftBehind == null && Flickers(hidden, key, now);
        if (Log.DebugEnabled)
        {
            LogMarkerAfterJump(what, logId, key, position, leftBehind != null, visible, now);
            LogMarkerAtArrival(what, logId, key, position, visible, now);
        }

        return visible;
    }

    // For the diagnostics log: the visible size of the clouds at a recent arrival, every quarter of a second
    private static void LogArrivalCloudSizes(double now)
    {
        if (!Log.DebugEnabled || RecentArrivals.Count == 0 || now - lastSizeLogAt < 0.25)
            return;

        lastSizeLogAt = now;
        RecentArrivals.RemoveAll(a => now - a.At > ArrivalLogSeconds + 2);
        foreach (var cloud in Clouds)
        {
            if (cloud.Kind == CloudKind.Origin || cloud.Finished)
                continue;

            foreach (var arrival in RecentArrivals)
            {
                if (Vector3D.Distance(cloud.Center, arrival.Target) > ArrivalLogDistance || now - arrival.At > ArrivalLogSeconds)
                    continue;

                Log.Debug($"Cloud size: {cloud.Kind} at t{now - arrival.At:+0.00;-0.00}: radius {cloud.VisibleRadius(now):0.0} m");
                break;
            }
        }
    }

    // The last state logged of each marker near a recent arrival: visible
    private static readonly Dictionary<long, bool> ArrivalMarkerStates = new Dictionary<long, bool>();

    private static void LogMarkerAtArrival(string what, long logId, long key, Vector3D position, bool visible, double now)
    {
        foreach (var arrival in RecentArrivals)
        {
            var since = now - arrival.At;
            var distance = Vector3D.Distance(position, arrival.Target);
            if (since < 0 || since > ArrivalLogSeconds || distance > ArrivalLogDistance)
                continue;

            if (ArrivalMarkerStates.TryGetValue(logId, out var last) && last == visible)
                return;

            ArrivalMarkerStates[logId] = visible;
            Log.Debug($"Marker at the arrival: {what} {logId} of {key} {(visible ? "shown" : "hidden")}, {distance:0} m from the arrival at t{since:+0.00}");
            return;
        }
    }

    // The last state logged of each marker after its ship's jump: (left behind, visible)
    private static readonly Dictionary<long, (bool, bool)> MarkerStates = new Dictionary<long, (bool, bool)>();

    private static void LogMarkerAfterJump(string what, long logId, long key, Vector3D position, bool leftBehind, bool visible, double now)
    {
        if (!Log.DebugEnabled)
            return;

        foreach (var cloud in Clouds)
        {
            if (cloud.Kind != CloudKind.Origin || !cloud.Detached || cloud.Finished || !cloud.Carries(key))
                continue;

            if (MarkerStates.TryGetValue(logId, out var last) && last == (leftBehind, visible))
                return;

            MarkerStates[logId] = (leftBehind, visible);
            Log.Debug($"Marker after the jump: {what} {logId} of {key} {(visible ? "shown" : "hidden")}{(leftBehind ? ", left behind" : "")}, " +
                      $"{Vector3D.Distance(position, cloud.Departure):0} m from the departure, {Vector3D.Distance(position, cloud.Arrival):0} m " +
                      $"from the arrival at t{now - cloud.DetachedAt:+0.00;-0.00}");
            return;
        }
    }

    // The camera's own jump: in transit every marker on its HUD flickers out, not only those of the ship
    private static double cameraHiddenAt = double.NaN;
    private static float cameraHidden;

    private static float CameraMarkersHidden(double now)
    {
        if (now == cameraHiddenAt)
            return cameraHidden;

        cameraHiddenAt = now;
        cameraHidden = 0f;
        if (CameraGridId() is long id)
        {
            foreach (var cloud in Clouds)
                cameraHidden = Math.Max(cameraHidden, cloud.MarkersHidden(id, now));
        }

        return cameraHidden;
    }

    private static bool Flickers(float hidden, long key, double now)
    {
        if (hidden <= 0f)
            return true;
        if (hidden >= 1f)
            return false;

        // A flicker: on and off a few times a second, off for a longer share of each beat the more hidden, the beats
        // a little irregular (a random draw per frame could leave a clean cut)
        var beat = now * MarkerFlickerRate;
        var step = (long)Math.Floor(beat);
        var hash = unchecked((ulong)key ^ ((ulong)step * 0x9E3779B97F4A7C15UL));
        hash ^= hash >> 33;
        hash *= 0xFF51AFD7ED558CCDUL;
        hash ^= hash >> 33;
        var jitter = (hash & 0xFFFF) / 65536.0;
        return (beat - step) * (0.7 + 0.6 * jitter) % 1.0 >= hidden;
    }

    private const double MarkerFlickerRate = 6;

    /// <summary>Render frame, main thread, the same place mods draw their billboards from</summary>
    public static void Draw()
    {
        var now = Now;
        var target = 1f;
        var transmittance = 1f;
        if (session != null && Clouds.Count != 0 && MySector.MainCamera != null)
        {
            var config = Config.Current;
            var camera = CameraPosition;
            var sunDirection = (Vector3D)MySector.SunProperties.SunDirectionNormalized;
            var wrapped = 0f;
            Cloud wrappingCloud = null;
            foreach (var cloud in Clouds)
            {
                if (Log.DebugEnabled && cloud.Kind == CloudKind.Origin && now - lastWrapLogAt >= 0.25)
                {
                    lastWrapLogAt = now;
                    Log.Debug($"Camera cloud: third person jumping along {ThirdPersonJumpsWith(cloud)}, open air {cloud.IsOpenAt(camera)}, intensity {cloud.CameraCloudIntensity(now):0.00}, {Vector3D.Distance(camera, cloud.Center):0} m from the cloud's centre at t{now - cloud.JumpAt:+0.00;-0.00}");
                }

                if (config.CameraOverlay && cloud.Kind != CloudKind.Provisional && ThirdPersonJumpsWith(cloud) && cloud.IsOpenAt(camera))
                {
                    var intensity = cloud.CameraCloudIntensity(now);
                    if (intensity > wrapped)
                    {
                        wrapped = intensity;
                        wrappingCloud = cloud;
                    }
                }
            }

            // A camera wrapped in its own cloud sees nothing outside it, the other clouds neither
            foreach (var cloud in Clouds)
                cloud.Draw(now, camera, sunDirection, config.Darkness, config.Lightning, MaxDistance, 1f - wrapped, ref target);

            wrapped *= Math.Min(1f, config.Darkness);
            if (wrapped > 0.005f)
                CameraCloud.Draw(camera, wrapped, config.Lightning ? wrappingCloud.LightningIntensity(now) : 0f, now);
            else
                CameraCloud.Clear();

            // The estimate counts puffs as discs of their own opacity; in optical depth (-ln of the transmittance) the
            // light fog of the first seconds is below 1, the formed cloud 15-20. The sky still shows through that, and so
            // does the sun, a little dimmer; only a really thick fog (20-40) puts it out. Those in the jumping ship lose it
            // to the transit itself: out from 5.5 s to 3.5 s before the jump (before the markers go), back from 2 s after
            // it as the exit fog allows
            transmittance = target;
            var depth = -(float)Math.Log(Math.Max(target, 1e-30f));
            var dim = MathHelper.Clamp(depth / 8f, 0f, 1f);
            var gone = MathHelper.Clamp((depth - 20f) / 20f, 0f, 1f);
            var x2 = (1f - 0.15f * dim * dim * (3f - 2f * dim)) * (1f - gone * gone * (3f - 2f * gone)) * (1f - TransitHidesSun(CameraJumpTime(now)));

            // A camera wrapped in its own cloud is in the fog dimension: no sun there at all
            var y = MathHelper.Clamp(wrapped / 0.5f, 0f, 1f);
            target = x2 * (1f - y * y * (3f - 2f * y));
        }
        else
        {
            CameraCloud.Clear();
        }

        // The sun disappears quickly into a cloud and comes back slowly, without flickering from one frame to the next
        var dt = (float)MathHelper.Clamp(now - lastDrawAt, 0, 0.1);
        lastDrawAt = now;
        SunVisibility += (target - SunVisibility) * Math.Min(1f, dt / (target < SunVisibility ? 0.08f : 0.6f));
        if (target >= 0.999f && SunVisibility > 0.995f)
            SunVisibility = 1f;

        if (transmittance < 1f && now - lastSunLogAt >= 0.25)
        {
            lastSunLogAt = now;
            Log.Debug($"Sun: transmittance {transmittance:0.000}, depth {-Math.Log(Math.Max(transmittance, 1e-30f)):0.00}, visibility {SunVisibility:0.000} at t{CameraJumpTime(now):+0.00;-0.00}");
        }
    }

    private static float TransitHidesSun(double? t)
    {
        if (t is not double since)
            return 0f;

        var x = MathHelper.Clamp((float)(since < 0 ? (since + 5.5) / 2.0 : 1.0 - (since - 2.0) / 1.5), 0f, 1f);
        return x * x * (3f - 2f * x);
    }

    /// <summary>How much of the sun disc and its glare can be seen through the clouds from the camera, 1 when there is no cloud in the way</summary>
    public static float SunVisibility { get; private set; } = 1f;

    private static double lastDrawAt;
    private static double lastUpdateAt = double.NaN;
    private static double lastSunLogAt;
    private static double lastWrapLogAt;
    private static bool cameraMarkersOut;

    public static void Reset()
    {
        foreach (var hidden in HiddenGrids)
            Show(hidden.Grid);

        HiddenGrids.Clear();
        foreach (var cloud in Clouds)
            cloud.StopSound();
        Clouds.Clear();
        MarkerStates.Clear();
        ArrivalMarkerStates.Clear();
        RecentArrivals.Clear();
        JumpSounds.Reset();
        MusicDuck.Restore();
        cameraMarkersOut = false;
        lastUpdateAt = double.NaN;
        Origins.Clear();
        Arrivals.Clear();
        CameraCloud.Clear();
        SunVisibility = 1f;
        session = null;
    }

    #endregion

    #region Camera

    /// <summary>
    /// The camera that gets a <see cref="CameraCloud"/>: the third person camera of a ship controller on the jumping grid
    /// group. It jumps with the ship but can be anywhere around it, outside the ship's cloud or where that cloud is thin.
    /// Every other view of the ship (first person, camera blocks) is inside or behind the ship's own cloud, which masks
    /// the jump; the spectator and cameras elsewhere are observers.
    /// </summary>
    private static bool ThirdPersonJumpsWith(Cloud cloud)
    {
        var session = MySession.Static;
        if (session == null || session.GetCameraControllerEnum() != MyCameraControllerEnum.ThirdPersonSpectator)
            return false;

        return session.ControlledEntity?.Entity?.GetTopMostParent() is MyCubeGrid grid && cloud.GroupIds.Contains(grid.EntityId);
    }

    /// <summary>Whether the camera is teleported with the grid: the view of something on its group (a ship controller, a camera block)</summary>
    private static bool CameraJumpsWith(MyCubeGrid grid)
    {
        if (CameraGridId() is not long id)
            return false;

        if (id == grid.EntityId)
            return true;

        var group = MyCubeGridGroups.Static.Physical.GetGroup(grid);
        if (group == null)
            return false;

        foreach (var node in group.Nodes)
        {
            if (node.NodeData?.EntityId == id)
                return true;
        }

        return false;
    }

    /// <summary>Whether the grid of the camera (the seat's or camera block's ship) reaches into a sphere</summary>
    public static bool CameraGridTouches(BoundingSphereD sphere) =>
        CameraGridId() is long id && MyEntities.TryGetEntityById(id, out var grid) && grid.PositionComp.WorldAABB.Intersects(sphere);

    private static long? CameraGridId()
    {
        var controller = MySession.Static?.CameraController;
        switch (controller)
        {
            case null:
            case MySpectatorCameraController:
                return null;
            case MyCameraBlock cameraBlock:
                return cameraBlock.CubeGrid?.EntityId;
        }

        return (controller.Entity as MyEntity)?.GetTopMostParent() is MyCubeGrid grid ? grid.EntityId : null;
    }

    #endregion
}
