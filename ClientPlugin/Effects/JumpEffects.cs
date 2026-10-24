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

    // The HUD message of the camera's own jump (it replaces the vanilla countdown): the gate manifests until TransitFrom
    // before the jump, the ship is in transit from then until TransitFrom after it (the smoke hides it then, and the
    // world outside), and the jump is complete for CompleteFor more
    private const double TransitFrom = 5;
    private const double CompleteFor = 3;

    private static string JumpMessage(double? t)
    {
        if (t is not double since || since >= TransitFrom + CompleteFor)
            return null;
        if (since >= TransitFrom)
            return "Jump Complete";
        return since >= -TransitFrom ? "In Transit" : "Manifesting Gate";
    }

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
            Log.Debug($"{cloud.Kind} cloud of {cloud.Grid?.DisplayName ?? "(not replicated yet)"} finished, {cloud.ClampedByShip} puff draws cut down at the jumping grids' sealed air, {cloud.ClampedByObstacle} at other grids' sealed air; discharges heard so far: {JumpSounds.StrikesPlayed} strikes, {JumpSounds.SnapsPlayed} snaps, {JumpSounds.FlashesPlayed} flashes; echoes: {JumpSounds.WailsPlayed} wails, {JumpSounds.EchoesPlayed} roars");
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
            SmokeLight.Update();
            var sunDirection = (Vector3D)MySector.SunProperties.SunDirectionNormalized;
            var wrapped = 0f;
            Cloud wrappingCloud = null;
            foreach (var cloud in Clouds)
            {
                if (Log.DebugEnabled && cloud.Kind == CloudKind.Origin && now - lastWrapLogAt >= 0.25)
                {
                    lastWrapLogAt = now;
                    Log.Debug($"Camera cloud: outside view jumping along {OutsideViewJumpsWith(cloud)}, open air {CameraInOpenAir(cloud, camera)}, intensity {cloud.CameraCloudIntensity(now):0.00}, {Vector3D.Distance(camera, cloud.Center):0} m from the cloud's centre, {NearestGrid(cloud, camera):0.0} m from the ship at t{now - cloud.JumpAt:+0.00;-0.00}");
                }

                if (config.CameraOverlay && cloud.Kind != CloudKind.Provisional && OutsideViewJumpsWith(cloud) && CameraInOpenAir(cloud, camera))
                {
                    var intensity = cloud.CameraCloudIntensity(now);
                    if (intensity > wrapped)
                    {
                        wrapped = intensity;
                        wrappingCloud = cloud;
                    }
                }
            }

            if (wrappingCloud != null)
                wrappingCloud.WrappedCamera = true;

            // A camera wrapped in its own cloud sees nothing outside it, the other clouds neither. Its ship is in the fog with
            // it, which forms with the ship's cloud from the countdown's start: the ship's own cloud is not drawn for this
            // camera (its dense puffs at the hull would hide the ship, and let it come back as they faded), not even once
            // the fog has cleared after the arrival (what is left of it would come in at once). A camera moved into a
            // sealed room of the ship sees the cloud through the windows, as from the cockpit
            foreach (var cloud in Clouds)
                cloud.Draw(now, camera, sunDirection, config.Darkness, config.Lightning, MaxDistance, ReplacedForCamera(cloud, wrappingCloud, camera) ? 0f : 1f - wrapped, ref target);

            // A camera inside a cloud (a cockpit, a camera block, the hangar, anyone standing in it) sees nothing outside
            // the smoke either. The third person camera in its void has the void's fog instead
            var forward = MySector.MainCamera.WorldMatrix.Forward;
            var inside = 0f;
            var insideDistance = 0.0;
            Cloud insideCloud = null;
            foreach (var cloud in Clouds)
            {
                if (ReplacedForCamera(cloud, wrappingCloud, camera))
                    continue;

                var density = cloud.InsideDensity(camera, forward, now, out var distance);
                if (density > inside)
                {
                    inside = density;
                    insideDistance = distance;
                    insideCloud = cloud;
                }
            }

            // In a sealed room (a cockpit, a bridge camera) the room and the ship around it stay in front of the backdrop.
            // A camera in the smoke itself (a camera block on the hull, anyone standing in it) sees nothing but smoke: the
            // hull and the lights next to it would show through the puffs' gaps, so the backdrop comes in front of the
            // ship, as near as that takes (the nearer, the more of the smoke's light it outweighs). Not for the third
            // person camera in the fog with the ship: it sees the ship
            var farBackdrop = Math.Max(Math.Max(insideDistance, CameraGridFarSide(camera, forward)), MinBackdropDistance);
            var backdropDistance = insideCloud != null && insideCloud != wrappingCloud && insideCloud.IsOpenAt(camera)
                ? MathHelper.Clamp(NearestGrid(insideCloud, camera) - ShipClearance, NearestBackdropDistance, farBackdrop)
                : farBackdrop;
            var backdrop = Backdrop.Opacity(inside * (1f - wrapped) * Math.Min(1f, config.Darkness));
            if (backdrop > 0.003f)
                Backdrop.Draw(backdropDistance, backdrop);

            if (Log.DebugEnabled && (inside > 0f || wrapped > 0f) && now - lastBackdropLogAt >= 0.25)
            {
                lastBackdropLogAt = now;
                Log.Debug($"Backdrop: inside a cloud {inside:0.00}, opacity {backdrop:0.00} at {insideDistance:0} m; camera cloud {wrapped:0.00} at t{CameraJumpTime(now):+0.00;-0.00}");
            }

            wrapped *= Math.Min(1f, config.Darkness);
            if (wrapped > 0.005f)
                CameraCloud.Draw(camera, wrappingCloud, wrapped, config.Lightning ? wrappingCloud.LightningIntensity(now) : 0f, now);
            else
                CameraCloud.Clear();

            // The grids that do not jump go out of the view of a camera on board as the world outside does: for a camera
            // in its fog as what is outside the void, for any other as its ship disappears in its cloud for those outside
            var ownCloud = wrapped > 0.005f ? wrappingCloud : CameraCloudOnBoard();
            var outsideCover = wrapped > 0.005f ? 1f - CameraCloud.OutsideTransmission(wrapped) : ownCloud?.ShipCover(now) ?? 0f;
            var outsideRange = wrapped > 0.005f ? CameraCloud.Current.Radius : (ownCloud?.PortalRadius ?? 0) * 1.4;
            OutsideGrids.Update(ownCloud, outsideCover, camera, outsideRange, now);

            // The flames and glares no smoke hides fade with the ship's cloud, and for a camera inside the smoke those
            // beyond its backdrop with the backdrop. For a wrapped camera they fade into its fog like everything in it,
            // its own ship's included
            if (wrapped > 0.005f)
                Cover.Update(Clouds, now, camera, wrappingCloud, CameraCloud.Current, 0.0, 1f);
            else
                Cover.Update(Clouds, now, camera, null, default, backdropDistance, 1f - backdrop);

            // The estimate counts puffs as discs of their own opacity; in optical depth (-ln of the transmittance) the
            // light fog of the first seconds is below 1, the formed cloud 15-20, where its middle hides the sky. The sun
            // goes out behind it as the sky does, a few times later than the estimate says (it overlaps the puffs' soft
            // discs): kept at 85 % up to a depth of 20, its glare shone over a cloud that hid everything else, which then
            // looked thin in sunlight. Those in the jumping ship lose it to the transit itself: out from 5.5 s to 3.5 s
            // before the jump (before the markers go), back from 2 s after it as the exit fog allows
            transmittance = target;
            var depth = -(float)Math.Log(Math.Max(target, 1e-30f));
            var x2 = (float)Math.Exp(-depth / SunDepthScale) * (1f - TransitHidesSun(CameraJumpTime(now)));

            // A camera wrapped in its own cloud is in the void: the sun is outside it, behind its fog
            target = x2 * CameraCloud.OutsideTransmission(wrapped) * (1f - backdrop);
        }
        else
        {
            CameraCloud.Clear();
            OutsideGrids.Clear();
            Cover.Clear();
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

    // The ship's own cloud for an outside view of it in the void: its fog stands in for it, while the camera is in the
    // open air around the ship
    private static bool ReplacedForCamera(Cloud cloud, Cloud wrappingCloud, Vector3D camera) =>
        cloud == wrappingCloud || cloud.WrappedCamera && !cloud.Detached && OutsideViewJumpsWith(cloud) && CameraInOpenAir(cloud, camera);

    private static float TransitHidesSun(double? t)
    {
        if (t is not double since)
            return 0f;

        var x = MathHelper.Clamp((float)(since < 0 ? (since + 5.5) / 2.0 : 1.0 - (since - 2.0) / 1.5), 0f, 1f);
        return x * x * (3f - 2f * x);
    }

    /// <summary>How much of the sun disc and its glare can be seen through the clouds from the camera, 1 when there is no cloud in the way</summary>
    public static float SunVisibility { get; private set; } = 1f;

    // The sun behind a cloud keeps exp(-depth / SunDepthScale) of its light, for the estimated optical depth
    private const float SunDepthScale = 3f;

    private static double lastDrawAt;
    private static double lastUpdateAt = double.NaN;
    private static double lastSunLogAt;
    private static double lastWrapLogAt;
    private static double lastBackdropLogAt;
    private static bool cameraMarkersOut;

    // The backdrop of a camera inside a cloud is never nearer than this, of a camera in the smoke itself this near
    private const double MinBackdropDistance = 8;
    private const double NearestBackdropDistance = 1.2;

    // A backdrop in front of the ship comes this much nearer than its nearest grid
    private const double ShipClearance = 2.0;

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
        OutsideGrids.Clear();
        Cover.Clear();
        SunVisibility = 1f;
        session = null;
    }

    #endregion

    #region Camera

    /// <summary>
    /// The cameras that get a <see cref="CameraCloud"/> while in the open air around the ship: the third person camera of
    /// a ship controller on the jumping grid group, and a camera block on it. They jump with the ship and look at it from
    /// outside, from anywhere around it, outside the ship's cloud or where that cloud is thin. First person views (a
    /// cockpit, a camera in a sealed room) are inside the ship's own cloud, which masks the jump; the spectator and
    /// cameras elsewhere are observers.
    /// </summary>
    private static bool OutsideViewJumpsWith(Cloud cloud)
    {
        var session = MySession.Static;
        if (session == null)
            return false;

        if (session.CameraController is MyCameraBlock cameraBlock)
            return cameraBlock.CubeGrid != null && cloud.GroupIds.Contains(cameraBlock.CubeGrid.EntityId);

        if (session.GetCameraControllerEnum() != MyCameraControllerEnum.ThirdPersonSpectator)
            return false;

        return session.ControlledEntity?.Entity?.GetTopMostParent() is MyCubeGrid grid && cloud.GroupIds.Contains(grid.EntityId);
    }

    /// <summary>
    /// Whether the camera is in the air the smoke reaches around the ship: a camera block by the cell its lens looks
    /// into (the camera itself is inside its block), a camera in a sealed room or a block is not
    /// </summary>
    private static bool CameraInOpenAir(Cloud cloud, Vector3D camera)
    {
        if (MySession.Static?.CameraController is MyCameraBlock cameraBlock)
            return cloud.IsOpenAt(cameraBlock.PositionComp.WorldAABB.Center + cameraBlock.WorldMatrix.Forward * cameraBlock.CubeGrid.GridSize);

        return cloud.IsOpenAt(camera);
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

    /// <summary>How far the nearest grid of the cloud's jumping group is from the camera (its box in its own axes)</summary>
    private static double NearestGrid(Cloud cloud, Vector3D camera)
    {
        var nearest = double.MaxValue;
        foreach (var id in cloud.GroupIds)
        {
            if (MyEntities.TryGetEntityById(id, out MyCubeGrid grid) && !grid.MarkedForClose)
            {
                var local = Vector3D.Transform(camera, grid.PositionComp.WorldMatrixNormalizedInv);
                nearest = Math.Min(nearest, ((BoundingBoxD)grid.PositionComp.LocalAABB).Distance(local));
            }
        }

        return nearest;
    }

    /// <summary>How far along the view direction the camera's grid reaches (its bounding sphere's far side), 0 without one</summary>
    private static double CameraGridFarSide(Vector3D camera, Vector3D forward)
    {
        if (CameraGridId() is not long id || !MyEntities.TryGetEntityById(id, out var grid))
            return 0;

        var sphere = grid.PositionComp.WorldVolume;
        return Vector3D.Dot(sphere.Center - camera, forward) + sphere.Radius;
    }

    /// <summary>The cloud around the camera's ship while the ship is in it: before the jump, or at the exit</summary>
    private static Cloud CameraCloudOnBoard()
    {
        if (CameraGridId() is not long id)
            return null;

        foreach (var cloud in Clouds)
        {
            if (!cloud.Finished && !cloud.Detached && cloud.Kind != CloudKind.Provisional && cloud.GroupIds.Contains(id))
                return cloud;
        }

        return null;
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
