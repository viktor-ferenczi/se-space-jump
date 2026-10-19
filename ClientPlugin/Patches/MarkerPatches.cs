using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using ClientPlugin.Effects;
using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.Game.Gui;
using Sandbox.Game.GUI.HudViewers;
using Sandbox.Game.Screens.Helpers;
using VRage.Game.Entity;
using VRageMath;

namespace ClientPlugin.Patches;

/// <summary>
/// The HUD markers of a jumping ship (antennas, beacons, GPS points on it) would give the jump away while
/// the fog covers the ship: they flicker out with the cloud and back in at the exit. Those on board see every marker
/// flicker out for the transit: GPS points, unknown signals, points of interest, ore. See
/// <see cref="JumpEffects.MarkerVisible(MyEntity)"/>. The marker render collects the markers anew every frame.
/// </summary>
[HarmonyPatch(typeof(MyHudMarkerRender))]
[SuppressMessage("ReSharper", "UnusedType.Global")]
[SuppressMessage("ReSharper", "UnusedMember.Global")]
[SuppressMessage("ReSharper", "InconsistentNaming")]
public static class MarkerPatches
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(MyHudMarkerRender.AddEntity))]
    public static bool AddEntityPrefix(MyEntity entity) => JumpEffects.MarkerVisible(entity);

    [HarmonyPrefix]
    [HarmonyPatch(nameof(MyHudMarkerRender.AddGPS))]
    public static bool AddGPSPrefix(MyGps gps) =>
        gps.EntityId != 0 && MyEntities.TryGetEntityById(gps.EntityId, out var entity)
            ? JumpEffects.MarkerVisible(entity)
            : JumpEffects.MarkerVisible(gps.Coords);

    // Location markers without an entity, unknown signals among them. A remote broadcaster's marker (an antenna, a pilot's
    // suit, seen through a proxy) is one of them too: the receiver registers its HUD parameters with the entity id of its
    // block or character and a copy of its position, refreshed now and then, but not the entity
    [HarmonyPrefix]
    [HarmonyPatch(nameof(MyHudMarkerRender.AddProxyEntity))]
    public static bool AddProxyEntityPrefix(Vector3D worldPosition) => JumpEffects.MarkerVisible(worldPosition, LocationMarkerId(worldPosition));

    private static MyHudLocationMarkers locationMarkers;
    private static bool locationMarkerIdsBuilt;
    private static readonly Dictionary<Vector3D, long> LocationMarkerIds = new Dictionary<Vector3D, long>();

    [HarmonyPrefix]
    [HarmonyPatch(nameof(MyHudMarkerRender.DrawLocationMarkers))]
    public static void DrawLocationMarkersPrefix(MyHudLocationMarkers locationMarkers)
    {
        MarkerPatches.locationMarkers = locationMarkers;
        locationMarkerIdsBuilt = false;
    }

    // The entity id of the location marker drawn at this position this frame, read once a frame on demand (after the
    // render has applied the frame's marker changes)
    private static long? LocationMarkerId(Vector3D position)
    {
        if (!JumpEffects.Active || locationMarkers == null)
            return null;

        if (!locationMarkerIdsBuilt)
        {
            locationMarkerIdsBuilt = true;
            LocationMarkerIds.Clear();
            foreach (var marker in locationMarkers.MarkerEntities)
            {
                if (marker.Entity is not MyEntity)
                    LocationMarkerIds[marker.Position] = marker.EntityId;
            }
        }

        return LocationMarkerIds.TryGetValue(position, out var id) ? id : null;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(MyHudMarkerRender.AddPOI))]
    public static bool AddPOIPrefix(Vector3D worldPosition) => JumpEffects.MarkerVisible(worldPosition);

    [HarmonyPrefix]
    [HarmonyPatch(nameof(MyHudMarkerRender.AddOre))]
    public static bool AddOrePrefix(Vector3D worldPosition) => JumpEffects.MarkerVisible(worldPosition);
}
