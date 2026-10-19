using VRageMath;

namespace ClientPlugin.Effects;

// Hiding the HUD markers of the jumping ship in transit and at the arrival
public sealed partial class Cloud
{
    // In transit (from 3 s before the jump to 2 s after it) the markers are gone: they flicker out in the second
    // before it and back in in the second after it, around these times relative to the jump
    private const double MarkersOutAt = -3.5;
    private const double MarkersInAt = 2.5;

    /// <summary>
    /// Whether a marker of the jumping group at this position is still where the ship left, not yet at the arrival: a
    /// remote client learns of the new position a moment after the jump (the proxy of a broadcaster up to a second
    /// later), and a marker drawn there would show in the departure cloud
    /// </summary>
    public bool MarkerLeftBehind(long entityId, Vector3D position) =>
        Kind == CloudKind.Origin && Detached && !Finished && Carries(entityId) &&
        Vector3D.DistanceSquared(position, arrival) >= Vector3D.DistanceSquared(position, detachedTranslation);

    // One flicker, in or out, lasts twice this
    private const double MarkerFlickerHalf = 0.5;

    /// <summary>
    /// 0..1, how likely the HUD markers of a grid of this cloud are hidden now (a flicker draws them against it frame by
    /// frame); 0 when the cloud has no say: the markers flicker out just before the transit (t-3), stay hidden through
    /// it and flicker back in just after it (t+2) at the exit; an aborted jump brings them back at once
    /// </summary>
    public float MarkersHidden(long gridId, double now)
    {
        if (!Carries(gridId) || Finished)
            return 0f;

        switch (Kind)
        {
            case CloudKind.Origin when Aborted:
                return Flicker(abortedAt - (JumpAt + MarkersOutAt)) * (1f - Flicker(now - abortedAt - MarkerFlickerHalf));

            case CloudKind.Origin when Detached:
                return 0f;

            case CloudKind.Origin:
                return Flicker(now - (JumpAt + MarkersOutAt));

            default:
                return 1f - Flicker(now - (ArrivedAt + MarkersInAt));
        }
    }

    /// <summary>
    /// 0..1 as <see cref="MarkersHidden"/>, for any marker at this position: an arriving ship's markers can show before
    /// its grid does on a client that learned of the jump from the broadcast (the proxies of its antennas and of its
    /// pilots, which name only their block or character). The vanilla jump keeps arrivals clear of everything else, so
    /// whatever is inside an arrival cloud's bubble is the arriving ship's until the markers flicker in
    /// </summary>
    public float MarkersHiddenNear(Vector3D position, double now)
    {
        if (Kind == CloudKind.Origin || Finished || Shape == null)
            return 0f;

        var reach = ArrivalMarkerReach * Shape.BubbleSemiAxes.AbsMax();
        if (Vector3D.DistanceSquared(position, Center) > reach * reach)
            return 0f;

        return 1f - Flicker(now - (ArrivedAt + MarkersInAt));
    }

    // Markers this far from an arrival cloud's centre (in bubble radii) are the arriving ship's
    private const double ArrivalMarkerReach = 1.5;

    // 0 well before a flicker centred at 0, 1 well after it
    private static float Flicker(double t) => SmoothStep((float)((t + MarkerFlickerHalf) / (2 * MarkerFlickerHalf)));
}
