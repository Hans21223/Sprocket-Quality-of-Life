using System.Numerics;
using SprocketQoL;
using static SprocketQoL.BuildPrediction;

static class BuildPredictionTests
{
    static int checks;
    static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
    static void Near(Vector3 actual, Vector3 expected, float tolerance, string message) =>
        Check(Vector3.Distance(actual, expected) <= tolerance, $"{message}: {actual} != {expected}");
    static Projection Project(Vector3 value) => new(new Vector2(value.X * 100, value.Y * 100), value.Z + 10);
    static Projection Pixels(Vector3 value) => new(new Vector2(value.X, value.Y), value.Z + 100);

    internal static void Run()
    {
        checks = 0;
        var target = new Target(5, Vector3.UnitX);
        var query = new Query(Vector3.Zero, new Vector3(.98f, 0, 0));
        var hit = Resolve(new[] { target }, query, Project)!.Value;
        Check(hit.TargetId == 5 && hit.Coincident, "moving point A near B predicts exact point coincidence");
        Near(hit.Delta, Vector3.UnitX, 1e-6f, "suggested displacement moves the original anchor to B");
        Near(hit.Correction, new Vector3(.02f, 0, 0), 1e-6f, "correction is relative to the current native drag");
        Near(hit.AnchorPosition, target.Position, 1e-6f, "the preview anchor is at the target");
        Check(Math.Abs(hit.DistancePixels - 2) < 1e-4f, "candidate distance uses physical projected pixels");

        var anchor = new Vector3(1, 2, 3);
        var face = new[] { anchor + new Vector3(-1, -1, 0), anchor + new Vector3(1, -1, 0), anchor + new Vector3(1, 1, 0), anchor + new Vector3(-1, 1, 0) };
        var faceTarget = new Target(-7, anchor + new Vector3(2, 0, 0));
        var faceHit = Resolve(new[] { faceTarget }, new Query(anchor, new Vector3(1.96f, .01f, 0)), Project)!.Value;
        Check(faceHit.TargetId == -7, "negative identifiers support face-centre targets");
        var translated = face.Select(point => point + faceHit.Delta).ToArray();
        Near(translated.Aggregate(Vector3.Zero, (sum, point) => sum + point) / translated.Length,
            faceTarget.Position, 1e-6f, "a face translates as a group using its active pivot");
        for (int i = 0; i < face.Length; i++) for (int j = 0; j < face.Length; j++)
            Near(translated[i] - translated[j], face[i] - face[j], 1e-6f, "prediction preserves face shape and vertex offsets");
        Check(face.SequenceEqual(new[] { anchor + new Vector3(-1, -1, 0), anchor + new Vector3(1, -1, 0), anchor + new Vector3(1, 1, 0), anchor + new Vector3(-1, 1, 0) }),
            "resolving predictions never mutates original selected points");

        var constrainedTarget = new Target(1, new Vector3(1, .03f, .04f));
        var xHit = Resolve(new[] { constrainedTarget }, query with { AllowedAxes = AxisMask.X }, Project)!.Value;
        Near(xHit.Correction, new Vector3(.02f, 0, 0), 1e-6f, "axis lock corrects only the permitted local axis");
        Check(!xHit.Coincident && xHit.AllowedAxes == AxisMask.X, "axis alignment is not mislabeled as full vertex coincidence");
        var xyHit = Resolve(new[] { constrainedTarget }, query with { AllowedAxes = AxisMask.X | AxisMask.Y }, Project)!.Value;
        Near(xyHit.Correction, new Vector3(.02f, .03f, 0), 1e-6f, "plane lock keeps the forbidden axis unchanged");
        foreach (var axes in new[] { AxisMask.X, AxisMask.Y, AxisMask.Z, AxisMask.X | AxisMask.Y, AxisMask.X | AxisMask.Z, AxisMask.Y | AxisMask.Z, AxisMask.All })
        {
            var h = Resolve(new[] { constrainedTarget }, query with { AllowedAxes = axes }, Project)!.Value;
            if ((axes & AxisMask.X) == 0) Check(h.Correction.X == 0, "disabled X axis remains exact zero");
            if ((axes & AxisMask.Y) == 0) Check(h.Correction.Y == 0, "disabled Y axis remains exact zero");
            if ((axes & AxisMask.Z) == 0) Check(h.Correction.Z == 0, "disabled Z axis remains exact zero");
        }
        // A rotated native gizmo can constrain the local correction in another frame.
        var direction = Vector3.Normalize(new Vector3(1, 1, 0));
        Vector3 RotatedConstraint(Vector3 value) => direction * Vector3.Dot(value, direction);
        var rotated = Resolve(new[] { constrainedTarget }, query, Project, constrainCorrection: RotatedConstraint)!.Value;
        Near(Vector3.Cross(rotated.Correction, direction), Vector3.Zero, 1e-6f, "native rotated-axis callback determines the actual correction direction");
        Check(Resolve(new[] { constrainedTarget }, query, Project, constrainCorrection: _ => Vector3.Zero) == null,
            "a constraint that forbids all useful movement produces no fresh prediction");
        Check(Resolve(new[] { constrainedTarget }, query, Project, constrainCorrection: _ => new Vector3(float.NaN)) == null,
            "invalid native constraint output fails safely");
        Check(Resolve(new[] { constrainedTarget }, query, Project, constrainCorrection: value => value * 100) == null,
            "native constraints cannot bypass the maximum correction distance");
        var beforeConstraint = new Target(5, new Vector3(1, 0, 0));
        Check(Resolve(new[] { beforeConstraint }, query with { MaxCorrection = 10 }, Project,
            constrainCorrection: _ => new Vector3(0, 1, 0)) == null,
            "the corrected anchor must also stay inside the pixel tolerance after native constraints");

        Check(Resolve(new[] { target }, query with { RawDelta = new Vector3(.7f, 0, 0), MaxCorrection = 1 }, Project) == null,
            "targets outside the pixel radius cannot pull the selection");
        Check(Resolve(new[] { new Target(9, new Vector3(.98f, 0, 1000)) }, query, Project) == null,
            "a target at the same pixel but far behind the selection cannot cause a depth jump");
        Check(Resolve(new[] { new Target(9, new Vector3(.98f, 0, 1000)) }, query with { AllowedAxes = AxisMask.X }, Project) == null,
            "a distant alignment with zero permitted correction is not a new suggestion");
        Check(Resolve(new[] { target }, query with { MaxCorrection = .01f }, Project) == null,
            "world correction limit is enforced independently from the pixel radius");
        Check(Resolve(new[] { target }, query, Project, excluded: new HashSet<int> { 5 }) == null,
            "selected and mirrored target IDs can be explicitly excluded");
        Check(Resolve(new[] { target with { Visible = false } }, query, Project) == null, "hidden geometry does not attract prediction");
        Check(Resolve(new[] { target }, query, _ => new Projection(Vector2.Zero, -1)) == null, "anchors behind the camera cannot predict");
        Check(Resolve(new[] { new Target(3, new Vector3(1, 0, -11)) }, query with { MaxCorrection = 20 }, Project) == null,
            "targets behind the camera are excluded");
        Check(Resolve(new[] { target }, query with { RawDelta = target.Position }, Project) == null,
            "already coincident points do not generate distracting fresh zero-correction suggestions");
        var atTarget = Resolve(new[] { target }, query with { RawDelta = target.Position }, Project, hit)!.Value;
        Check(atTarget.TargetId == hit.TargetId && atTarget.Correction == Vector3.Zero,
            "an existing prediction stays stable when the raw drag reaches exact coincidence");

        var hysteresisTargets = new[] { new Target(1, new Vector3(10, 0, 0)), new Target(2, new Vector3(12, 0, 0)) };
        var hysteresisQuery = new Query(Vector3.Zero, new Vector3(10.8f, 0, 0), RadiusPixels: 4, MaxCorrection: 10);
        var first = Resolve(hysteresisTargets, hysteresisQuery, Pixels)!.Value;
        Check(first.TargetId == 1, "the nearest target starts the suggestion");
        Check(Resolve(hysteresisTargets, hysteresisQuery with { RawDelta = new Vector3(11.2f, 0, 0) }, Pixels, first)?.TargetId == 1,
            "small pointer jitter does not flicker between neighboring targets");
        Check(Resolve(hysteresisTargets, hysteresisQuery with { RawDelta = new Vector3(11.9f, 0, 0) }, Pixels, first)?.TargetId == 2,
            "an appreciably closer target releases the previous suggestion");
        var one = new[] { hysteresisTargets[0] };
        Check(Resolve(one, hysteresisQuery with { RawDelta = new Vector3(15, 0, 0) }, Pixels, first)?.TargetId == 1,
            "a held target has a slightly larger release radius");
        Check(Resolve(one, hysteresisQuery with { RawDelta = new Vector3(15, 0, 0) }, Pixels) == null,
            "a new target cannot use the expanded hold radius");
        Check(Resolve(one, hysteresisQuery with { RawDelta = new Vector3(16, 0, 0) }, Pixels, first) == null,
            "moving beyond the release radius removes a held prediction");
        Check(Resolve(one, hysteresisQuery with { RawDelta = new Vector3(15, 0, 0), MaxCorrection = 4 }, Pixels, first) == null,
            "holding never relaxes the world-space no-jump limit");
        Check(Resolve(one, hysteresisQuery with { RawDelta = new Vector3(15, 0, 0), AllowedAxes = AxisMask.X }, Pixels, first) == null,
            "changing the gizmo constraint invalidates old hysteresis");

        var visibilityTargets = new[] { new Target(1, new Vector3(.99f, 0, 0)), new Target(2, new Vector3(1.02f, 0, 0)), new Target(3, new Vector3(1.04f, 0, 0)) };
        var visits = new List<int>();
        var visibleFallback = Resolve(visibilityTargets, query, Project, accept: candidate => { visits.Add(candidate.TargetId); return candidate.TargetId != 1; });
        Check(visibleFallback?.TargetId == 2 && visits.SequenceEqual(new[] { 1, 2 }),
            "visibility checks the ranked shortlist and falls back past the nearest occluded candidate");
        visits.Clear();
        Check(Resolve(visibilityTargets, query, Project, accept: candidate => { visits.Add(candidate.TargetId); return true; })?.TargetId == 1
            && visits.SequenceEqual(new[] { 1 }), "visibility checks stop as soon as the nearest eligible target is accepted");
        var formerlyVisible = Resolve(visibilityTargets, query, Project)!.Value;
        visits.Clear();
        Check(Resolve(visibilityTargets, query, Project, formerlyVisible, accept: candidate => { visits.Add(candidate.TargetId); return candidate.TargetId != 1; })?.TargetId == 2,
            "hysteresis releases the previous target immediately when it becomes occluded");
        Check(visits.SequenceEqual(new[] { 1, 2 }), "held target visibility is cached rather than raycast twice");
        visits.Clear();
        Check(Resolve(one, hysteresisQuery with { RawDelta = new Vector3(15, 0, 0) }, Pixels, first,
            accept: candidate => { visits.Add(candidate.TargetId); return false; }) == null && visits.SequenceEqual(new[] { 1 }),
            "a retained target outside the normal radius still needs to pass visibility");
        visits.Clear();
        Check(Resolve(visibilityTargets, query with { MaxCorrection = .001f }, Project,
            accept: candidate => { visits.Add(candidate.TargetId); return true; }) == null && visits.Count == 0,
            "expensive visibility checks never run for candidates rejected by the world-distance guard");

        var ties = new[] { new Target(9, new Vector3(1, .01f, 0)), new Target(4, new Vector3(1, -.01f, 0)), new Target(2, new Vector3(1, -.01f, 0)) };
        foreach (var order in Permutations(ties)) Check(Resolve(order, query, Project)?.TargetId == 2,
            "equal-distance candidates resolve deterministically regardless of mesh enumeration order");
        var strongNearest = new[] { new Target(1, new Vector3(1.001f, 0, 0)), new Target(2, new Vector3(1.01f, 0, 0)) };
        Check(Resolve(strongNearest, query, Project)?.TargetId == 1, "projected proximity outranks stable identifier");
        var chainedTies = new[] { new Target(3, new Vector3(1, 0, 0)), new Target(2, new Vector3(1.000008f, 0, 0)), new Target(1, new Vector3(1.000016f, 0, 0)) };
        var tieIds = Permutations(chainedTies).Select(order => Resolve(order, query, Project)?.TargetId).Distinct().ToArray();
        Check(tieIds.Length == 1, "subpixel tie groups remain transitive and order independent");

        // Uniform translation and rotation of model and camera cannot alter the
        // target choice or displacement. Scale changes include the safety distance.
        var rotations = new[] { Quaternion.Identity, Quaternion.CreateFromYawPitchRoll(.7f, -.4f, .3f), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI) };
        foreach (float scale in new[] { .01f, .2f, 1f, 12f, 100f }) foreach (var rotation in rotations)
        {
            var offset = new Vector3(2, -3, 7);
            Vector3 Transform(Vector3 point) => Vector3.Transform(point * scale, rotation) + offset;
            Vector3 TransformVector(Vector3 value) => Vector3.Transform(value * scale, rotation);
            Vector3 Inverse(Vector3 point) => Vector3.Transform(point - offset, Quaternion.Inverse(rotation)) / scale;
            var transformedQuery = query with { Anchor = Transform(query.Anchor), RawDelta = TransformVector(query.RawDelta), MaxCorrection = query.MaxCorrection * scale };
            var transformedHit = Resolve(new[] { target with { Position = Transform(target.Position) } }, transformedQuery, point => Project(Inverse(point)))!.Value;
            Check(transformedHit.TargetId == hit.TargetId, "model and camera transforms preserve target identity");
            Near(transformedHit.Delta, TransformVector(hit.Delta), 2e-4f * Math.Max(1, scale), "model and camera transforms preserve the suggested translation");
            Near(transformedHit.AnchorPosition, Transform(hit.AnchorPosition), 2e-4f * Math.Max(1, scale), "transformed prediction arrives at the same geometric target");
        }

        foreach (var invalid in new[] {
            query with { Anchor = new Vector3(float.NaN) }, query with { RawDelta = new Vector3(float.PositiveInfinity) },
            query with { AllowedAxes = AxisMask.None }, query with { AllowedAxes = (AxisMask)8 },
            query with { RadiusPixels = -1 }, query with { RadiusPixels = float.NaN }, query with { RadiusPixels = float.PositiveInfinity },
            query with { MaxCorrection = -1 }, query with { MaxCorrection = float.NaN }, query with { MaxCorrection = float.PositiveInfinity },
            query with { HoldRadiusFactor = .9f }, query with { HoldRadiusFactor = float.NaN }, query with { HoldRadiusFactor = float.PositiveInfinity },
            query with { SwitchMarginPixels = -1 }, query with { SwitchMarginPixels = float.NaN }, query with { SwitchMarginPixels = float.PositiveInfinity } })
            Check(Resolve(new[] { target }, invalid, Project) == null, "invalid queries fail without a geometry edit");
        foreach (var invalidTarget in new[] { target with { Position = new Vector3(float.NaN) }, target with { Position = new Vector3(float.PositiveInfinity) } })
            Check(Resolve(new[] { invalidTarget }, query, Project) == null, "invalid target geometry is ignored");
        foreach (var invalidProjection in new[] { new Projection(new Vector2(float.NaN), 1), new Projection(Vector2.Zero, 0), new Projection(Vector2.Zero, float.PositiveInfinity) })
            Check(Resolve(new[] { target }, query, _ => invalidProjection) == null, "invalid camera projection fails safely");
        Check(Resolve(null, query, Project) == null && Resolve(Array.Empty<Target>(), query, Project) == null && Resolve(new[] { target }, query, null) == null,
            "empty inputs and absent projection have no suggestion");
        Check(Resolve(new[] { target }, query with { MaxCorrection = 0 }, Project) == null, "zero maximum correction safely disables new snapping");
        Check(Resolve(new[] { target }, query with { RadiusPixels = 0 }, Project) == null, "zero pixel radius does not attract nearby points");
        var overflowQuery = new Query(new Vector3(float.MaxValue), new Vector3(float.MaxValue), MaxCorrection: float.MaxValue);
        Check(Resolve(new[] { target }, overflowQuery, Pixels) == null, "overflowed translated anchor is rejected");
        GeometrySafety();
        Console.WriteLine($"BUILD_PREDICTION_TESTS_OK: {checks} checks passed");
    }

    static void GeometrySafety()
    {
        // The source-side plate has plenty of space, but the mirrored point lies
        // only 1/32 m from its opposite edge. Binary fractions make the collapsed
        // case exactly collinear rather than depending on floating-point epsilon.
        var positions = new[] {
            new Vector3(1, 0, 0), new Vector3(2, 0, 0), new Vector3(1, 1, 0),
            new Vector3(-1, 0, 0), new Vector3(-1.03125f, 0, 0), new Vector3(-1.03125f, 1, 0)
        };
        var faces = new[] { new[] { 0, 1, 2 }, new[] { 3, 4, 5 } };
        var targets = new[] {
            new Target(1, new Vector3(1.0625f, 0, 0)),
            new Target(2, new Vector3(1.03125f, 0, 0)),
            new Target(3, new Vector3(1.015625f, 0, 0))
        };
        var query = new Query(positions[0], new Vector3(.05859375f, 0, 0));
        Dictionary<int, Vector3> MirroredMove(Vector3 delta)
        {
            Vector3 source = positions[0] + delta;
            return new() { [0] = source, [3] = new Vector3(-source.X, source.Y, source.Z) };
        }
        foreach (var target in targets)
            Check(MeshPlans.Folds(positions, faces.Take(1).ToArray(), new Dictionary<int, Vector3> { [0] = target.Position }) == null,
                "every mirror fixture target preserves the selected-side plate");
        Check(MeshPlans.Folds(positions, faces, MirroredMove(targets[0].Position - positions[0]))?.Contains("fold") == true,
            "the nearest target independently folds the mirrored-side plate");
        Check(MeshPlans.Folds(positions, faces, MirroredMove(targets[1].Position - positions[0]))?.Contains("no area") == true,
            "the second target independently collapses the mirrored-side plate");
        Check(MeshPlans.Folds(positions, faces, MirroredMove(targets[2].Position - positions[0])) == null,
            "the farther target preserves both source and mirror plates");
        Check(Resolve(targets, query, Project)?.TargetId == 1,
            "without a geometry acceptance check the closest mirror fixture target wins");
        var visits = new List<int>();
        var safe = Resolve(targets, query, Project, accept: candidate => {
            visits.Add(candidate.TargetId);
            return MeshPlans.Folds(positions, faces, MirroredMove(candidate.Delta)) == null;
        });
        Check(safe?.TargetId == 3 && visits.SequenceEqual(new[] { 1, 2, 3 }),
            "geometry acceptance skips a mirrored fold and collapse, then predicts the nearest safe target");
        Near(MirroredMove(safe!.Value.Delta)[3], new Vector3(-1.015625f, 0, 0), 0,
            "accepted displacement arrives at the safe mirrored position");

        // A proportionally influenced vertex is not in the explicit selection.
        // Its narrow triangle must participate in safety checks nevertheless.
        var followerPositions = new[] {
            positions[0], positions[1], positions[2],
            new Vector3(0, 1, 0), new Vector3(.015625f, 1, 0), new Vector3(.015625f, 2, 0)
        };
        var followerFaces = new[] { new[] { 0, 1, 2 }, new[] { 3, 4, 5 } };
        Dictionary<int, Vector3> InfluencedMove(Vector3 delta) => new() {
            [0] = followerPositions[0] + delta,
            [3] = followerPositions[3] + delta * .5f
        };
        Check(MeshPlans.Folds(followerPositions, followerFaces.Take(1).ToArray(), InfluencedMove(targets[0].Position - positions[0])) == null,
            "the closest proportional-edit target leaves the explicitly selected plate valid");
        Check(MeshPlans.Folds(followerPositions, followerFaces, InfluencedMove(targets[0].Position - positions[0]))?.Contains("fold") == true,
            "the same target folds the unselected follower's narrow triangle");
        Check(MeshPlans.Folds(followerPositions, followerFaces, InfluencedMove(targets[1].Position - positions[0]))?.Contains("no area") == true,
            "a half-strength proportional follower can collapse independently of the selected plate");
        visits.Clear();
        var followerSafe = Resolve(targets, query, Project, accept: candidate => {
            visits.Add(candidate.TargetId);
            return MeshPlans.Folds(followerPositions, followerFaces, InfluencedMove(candidate.Delta)) == null;
        });
        Check(followerSafe?.TargetId == 3 && visits.SequenceEqual(new[] { 1, 2, 3 }),
            "prediction includes proportional followers when falling back past unsafe geometry");
        Near(InfluencedMove(followerSafe!.Value.Delta)[3], new Vector3(.0078125f, 1, 0), 0,
            "safe prediction retains the follower's expected half-strength displacement");
        Check(Resolve(targets.Take(2), query, Project,
            accept: candidate => MeshPlans.Folds(followerPositions, followerFaces, InfluencedMove(candidate.Delta)) == null) == null,
            "no prediction is offered when every nearby target would fold or collapse an affected face");
        Check(positions[0] == new Vector3(1, 0, 0) && positions[3] == new Vector3(-1, 0, 0)
            && followerPositions[3] == new Vector3(0, 1, 0), "geometry acceptance never mutates source or follower positions");
    }

    static IEnumerable<T[]> Permutations<T>(T[] items)
    {
        if (items.Length == 0) { yield return Array.Empty<T>(); yield break; }
        for (int i = 0; i < items.Length; i++)
            foreach (var tail in Permutations(items.Where((_, j) => j != i).ToArray())) yield return new[] { items[i] }.Concat(tail).ToArray();
    }
}
