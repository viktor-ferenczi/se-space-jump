using System;
using System.Collections.Generic;
using ClientPlugin.Core;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using VRage.Game;
using VRageMath;

namespace ClientPlugin.Effects;

// The smoke gate around a third person camera that jumps with its ship
public sealed partial class Cloud
{
    // The gate forms this long after the ship's cloud started, fully formed a second before the jump as the camera's
    // view closes
    private const double GateDelay = 1.0;

    // Its smoke: a thick shell beyond the ship as the camera sees it (the camera's distance to the ship's far side plus
    // GateClearance), GateThickness of that radius deep; inside the shell a thin haze from GateHollow metres on, around
    // the camera and the ship, the ship showing through it as through the smoke outside a window
    private const int GateShellPuffs = 650;
    private const int GateHazePuffs = 260;
    private const double GateClearance = 6;
    private const double GateMinInner = 25;
    private const double GateThickness = 0.5;
    private const double GateHollow = 3;
    private const float GateHazeDensity = 0.45f;

    // The smoke around the camera stays unlit this far (m); the discharges flash this many times as often as in a cloud of
    // the gate's size: the void's smoke shows only where they light it
    private const double GateDark = 9;
    private const double GateFlashes = 4;

    // The discharges are seen in the camera's view (and a little around it), where the gate's smoke shows them: elsewhere
    // they only light the ship, from this far beyond its bounding sphere (m)
    private const double GateViewCone = 0.6;
    private const double GateLightClearance = 6;

    // How bright a discharge's glow in the gate is (HDR, times the flash colour)
    private const float GateGlow = 6f;
    private const float GateGlowOpacity = 0.4f;

    // The backdrop closing the view behind the gate, this many times as far as its shell's outside
    private const double GateBackdrop = 3;

    // The shell follows a zoom in this long (s), so its puffs do not jump with the camera
    private const double GateResize = 0.5;

    // The gate's ship: its origin cloud until the jump, its destination cloud after it
    private Cloud gateOf;
    private double gateInner, gateOuter;
    private float gateFormation;
    private double gateFollowedAt = double.NaN;
    private double lastGateLogAt;

    /// <summary>The ship's cloud the gate goes with</summary>
    public Cloud GateOf => gateOf;

    public static Cloud CreateCameraGate(Cloud ship, double now)
    {
        var gate = new Cloud(CloudKind.Camera, null, now, (int)(ship.GridId * 17 ^ (long)(now * 1000)))
        {
            StartedAt = now,
            obstacles = new List<Obstacle>(),
        };

        var shape = new CloudShape { BubbleCenter = Vector3D.Zero, BubbleSemiAxes = new Vector3D(GateMinInner) };
        var random = gate.random;
        for (var i = 0; i < GateShellPuffs + GateHazePuffs; i++)
        {
            var haze = i >= GateShellPuffs;
            Vector3 direction;
            do
            {
                direction = new Vector3(random.NextDouble() * 2 - 1, random.NextDouble() * 2 - 1, random.NextDouble() * 2 - 1);
            } while (direction.LengthSquared() > 1f || direction.LengthSquared() < 1e-4f);

            direction.Normalize();
            var rim = (float)random.NextDouble();
            shape.Puffs.Add(new Puff
            {
                Drift = direction,
                // Haze: evenly through the inside's volume; shell: the inner part denser, so the thick smoke starts there
                Rim = haze ? (float)Math.Pow(rim, 1.0 / 3.0) : rim * rim,
                Radius = haze ? 0.09f + 0.06f * (float)random.NextDouble() : 0.12f + 0.1f * (float)random.NextDouble(),
                Angle = (float)(random.NextDouble() * Math.PI * 2),
                Spin = (float)((random.NextDouble() - 0.5) * 0.4),
                Alpha = 0.55f + 0.45f * (float)random.NextDouble(),
                Seed = (float)random.NextDouble(),
                Material = (byte)random.Next(MaterialSources.Length),
                // The shell from its outside inwards, the haze last
                Order = haze ? 0.7f + 0.3f * (float)random.NextDouble() : 0.6f * (1f - rim),
                Haze = haze,
            });
        }

        gate.Shape = shape;
        gate.gateOf = ship;
        gate.GroupIds.UnionWith(ship.GroupIds);
        return gate;
    }

    /// <summary>Every frame the gate is drawn: around the camera, beyond the ship, as far as its ship's cloud lets it form</summary>
    public void Follow(Cloud ship, Vector3D camera, double now)
    {
        gateOf = ship;
        gateFormation = ship.GateFormation(now);
        frame = MatrixD.CreateTranslation(camera);

        var inner = GateMinInner;
        if (ship.Grid != null && !ship.Grid.MarkedForClose)
        {
            var sphere = ship.Grid.PositionComp.WorldVolume;
            foreach (var id in ship.GroupIds)
            {
                if (MyEntities.TryGetEntityById(id, out var grid) && !grid.MarkedForClose)
                    sphere = sphere.Include(grid.PositionComp.WorldVolume);
            }

            inner = Math.Max(inner, Vector3D.Distance(camera, sphere.Center) + sphere.Radius + GateClearance);
        }

        var dt = double.IsNaN(gateFollowedAt) ? double.PositiveInfinity : MathHelper.Clamp(now - gateFollowedAt, 0, 0.1);
        gateFollowedAt = now;
        var k = 1.0 - Math.Exp(-dt / GateResize);
        gateInner = gateInner <= 0 ? inner : gateInner + (inner - gateInner) * k;
        gateOuter = gateInner * (1 + GateThickness);
        Shape.BubbleSemiAxes = new Vector3D(gateOuter);
    }

    /// <summary>
    /// The discharges' light in the gate's smoke: glows of the flash colour where they flash. The puffs they light alone
    /// hardly show it: beyond 50 m the renderer weighs every transparent billboard over a pixel the same, so the lit ones
    /// are averaged with the unlit ones and the backdrop. A glow is a bright billboard of its own, with an opacity (the
    /// renderer drops a billboard without one); the hull hides a glow beyond it
    /// </summary>
    private void DrawGateGlows(double now, Vector3 tint)
    {
        foreach (var patch in flashes)
        {
            var brightness = patch.Brightness(now);
            if (brightness < 0.02f)
                continue;

            var position = Vector3D.Transform(patch.Local, frame);
            for (var layer = 0; layer < 2; layer++)
                MyTransparentGeometry.AddPointBillboard(Materials[(layer + 1) % Materials.Length], new Vector4(tint * (GateGlow * brightness), GateGlowOpacity * brightness), position,
                    patch.Radius * (0.8f - 0.25f * layer), patch.Phase + 2.4f * layer);
        }
    }

    // Whether a position in the gate's frame (relative to the camera) is in the camera's view, with a margin
    private static bool InGateView(Vector3D local)
    {
        var view = MySector.MainCamera;
        if (view == null)
            return true;

        var length = local.Length();
        var cone = Math.Cos(Math.Min(view.FieldOfView * Math.Max(1f, view.AspectRatio) * GateViewCone, Math.PI * 0.45));
        return length < 1e-3 || Vector3D.Dot(local / length, view.WorldMatrix.Forward) >= cone;
    }

    /// <summary>
    /// 0..1, how far the smoke gate of a third person camera on this cloud's ship has formed: after the cloud, fully
    /// formed a second before the jump; at the exit as dense as the cloud there, clearing with it
    /// </summary>
    public float GateFormation(double now)
    {
        if (!Started || Finished)
            return 0f;

        if (Kind == CloudKind.Origin)
        {
            var at = Aborted ? abortedAt : now;
            var formed = SmoothStep((float)((at - StartedAt - GateDelay) / Math.Max(1.0, JumpAt - 1.0 - StartedAt - GateDelay)));
            return Aborted ? formed * (1f - SmoothStep((float)(now - abortedAt) / 0.8f)) : Detached ? 0f : formed;
        }

        return CameraCloudIntensity(now);
    }

    // A puff of the gate now, in its frame (the camera's position, the world's axes)
    private void EvaluateGate(ref Puff puff, double now, out float alpha, out float radius, out Vector3D local)
    {
        double distance, size;
        if (puff.Haze)
        {
            distance = GateHollow + puff.Rim * (gateInner - GateHollow);
            size = gateInner;
        }
        else
        {
            distance = gateInner + puff.Rim * (gateOuter - gateInner);
            size = gateOuter;
        }

        // The smoke turns slowly around the camera
        var turn = Quaternion.CreateFromAxisAngle(Vector3.Up, (float)((now - CreatedAt) * (0.02 + 0.03 * puff.Seed)));
        local = (Vector3D)Vector3.Transform(puff.Drift, turn) * distance;
        radius = (float)(puff.Radius * size);
        var formed = SmoothStep((gateFormation * 1.6f - puff.Order) / 0.6f);
        alpha = puff.Alpha * PuffOpacity * (puff.Haze ? GateHazeDensity : 1f) * formed;
    }
}
