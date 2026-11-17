using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using StormChase.Protocol;

internal static partial class Showcase
{
    /// <summary>CPU-only checks use independent soundings, analytic vectors and deterministic campaign samples.</summary>
    internal static int VerifyAtmosphere(string output)
    {
        Directory.CreateDirectory(output);
        var checks = new List<object>();
        int failures = 0;
        void Check(string name, bool passed)
        {
            checks.Add(new { name, passed });
            if (!passed) { failures++; Console.Error.WriteLine("FAIL: " + name); }
        }
        static ImmutableArray<AtmosphereLevel> Fixture(float temperature, float dewPoint, float lapse,
            float cap = 0, bool curved = false, float translateEast = 0, float translateNorth = 0)
        {
            var levels = ImmutableArray.CreateBuilder<AtmosphereLevel>(97);
            for (int height = 0; height <= 12000; height += 125)
            {
                float km = height / 1000f;
                float t = temperature - lapse * Math.Min(km, 9) + Math.Max(0, km - 9);
                t += cap * MathF.Exp(-MathF.Pow((height - 1250) / 450f, 2));
                float td = Math.Min(t - .1f, dewPoint - 2 * Math.Min(km, 1.5f) - 6 * Math.Max(0, km - 1.5f));
                float pressure = 1000 * MathF.Exp(-height / 8000f);
                float u = 5 + .004f * height + translateEast;
                float v = 2 + (curved ? 9 * MathF.Sin(MathF.PI * Math.Clamp(km / 3, 0, 1)) : 0) + translateNorth;
                levels.Add(new(height, pressure, t, td, u, v));
            }
            return levels.MoveToImmutable();
        }

        var stable = StormChaseAtmosphere.Diagnose(Fixture(20, 5, 4));
        var unstable = StormChaseAtmosphere.Diagnose(Fixture(30, 24, 7.5f));
        var capped = StormChaseAtmosphere.Diagnose(Fixture(30, 24, 7.5f, cap: 8));
        var dry = StormChaseAtmosphere.Diagnose(Fixture(30, 10, 7.5f));
        var curved = StormChaseAtmosphere.Diagnose(Fixture(30, 24, 7.5f, curved: true));
        var translated = StormChaseAtmosphere.Diagnose(Fixture(30, 24, 7.5f, curved: true, translateEast: 11, translateNorth: -7));
        Check("stable sounding has negligible parcel CAPE", stable.Diagnostics.CapeJkg < 5);
        Check("moist unstable sounding has substantial integrated CAPE", unstable.Diagnostics.CapeJkg > 1500);
        Check("CAPE03 is a genuine subset of total buoyant energy", unstable.Diagnostics.Cape03KmJkg > 0
            && unstable.Diagnostics.Cape03KmJkg < unstable.Diagnostics.CapeJkg);
        Check("elevated cap increases diagnosed inhibition", capped.Diagnostics.CinJkg < unstable.Diagnostics.CinJkg - 10);
        Check("cap suppresses initiation despite buoyancy aloft", capped.Diagnostics.CapeJkg > 500
            && capped.Diagnostics.InitiationPotential01 < unstable.Diagnostics.InitiationPotential01);
        Check("dry surface raises condensation level", dry.Diagnostics.LclMeters > unstable.Diagnostics.LclMeters + 1000);
        Check("high condensation level suppresses fixed STP", dry.Diagnostics.LclMeters > 2000 && dry.Diagnostics.Stp == 0);
        Check("ordered pressure declines continuously", unstable.Levels.Zip(unstable.Levels.Skip(1)).All(pair => pair.First.PressureHpa > pair.Second.PressureHpa));
        Check("parcel ascent actually changes diagnosed level temperatures", unstable.Levels[^1].ParcelTemperatureCelsius < unstable.Levels[0].ParcelTemperatureCelsius - 40);
        Check("straight hodograph has exact 0-6km vector shear", Math.Abs(unstable.Diagnostics.Shear06KmMetersPerSecond - 24) < .001);
        Check("straight hodograph Bunkers right mover matches analytic 7.5m/s deviation", Math.Abs(unstable.Diagnostics.StormMotion.X - 17) < .001
            && Math.Abs(unstable.Diagnostics.StormMotion.Z + 5.5f) < .001);
        Check("straight hodograph origin-relative SRH is zero", Math.Abs(StormChaseAtmosphere.Helicity(unstable.Levels, new(0, 0, 2), 3000)) < .001);
        Check("curved hodograph yields increased storm-relative low-level helicity", curved.Diagnostics.Srh01KmM2S2 > unstable.Diagnostics.Srh01KmM2S2 + 30);
        Check("storm motion is Galilean invariant", Math.Abs(translated.Diagnostics.StormMotion.X - curved.Diagnostics.StormMotion.X - 11) < .002
            && Math.Abs(translated.Diagnostics.StormMotion.Z - curved.Diagnostics.StormMotion.Z + 7) < .002);
        Check("helicity is Galilean invariant", Math.Abs(translated.Diagnostics.Srh01KmM2S2 - curved.Diagnostics.Srh01KmM2S2) < .005
            && Math.Abs(translated.Diagnostics.Srh03KmM2S2 - curved.Diagnostics.Srh03KmM2S2) < .005);
        Check("SPC published fixed STP example is reproduced", Math.Abs(StormChaseAtmosphere.FixedLayerStp(3000, 750, 150, 25 * .51444445f) - 1.2861111f) < .00001);
        Check("STP deep-shear lower bound is enforced", StormChaseAtmosphere.FixedLayerStp(4000, 800, 300, 12.49f) == 0);
        Check("STP deep-shear upper term is capped", StormChaseAtmosphere.FixedLayerStp(4000, 800, 300, 40)
            == StormChaseAtmosphere.FixedLayerStp(4000, 800, 300, 100));

        // A completely independent saturated ascent reference published by MetPy:
        // 925/850/700/500/300/200hPa starting at 5C. The environmental temperatures
        // are immaterial to this parcel-temperature comparison.
        float[] referencePressure = [925, 850, 700, 500, 300, 200];
        float[] referenceHeight = [0, 750, 2250, 4500, 7500, 10000];
        float[] referenceTemperature = [5, .99635104f, -8.88958079f, -28.38862857f, -60.12003999f, -83.34321585f];
        var saturatedReference = StormChaseAtmosphere.Diagnose(referencePressure.Select((p, i) =>
            new AtmosphereLevel(referenceHeight[i], p, 5 - referenceHeight[i] * .006f, 5 - referenceHeight[i] * .006f, 4 + i, 1)).ToImmutableArray());
        Check("moist parcel integrates independent published pseudoadiabat", saturatedReference.Levels.Select((level, i) =>
            Math.Abs(level.ParcelTemperatureCelsius - referenceTemperature[i])).Max() < .6);

        var location = new NetVector3(6000, 0, 7000);
        var replay = StormChaseAtmosphere.Sample(73019, 2, 16.2f, location);
        var same = StormChaseAtmosphere.Sample(73019, 2, 16.2f, location);
        Check("profile seed/time/location replay is exact", replay.Diagnostics == same.Diagnostics && replay.Levels.SequenceEqual(same.Levels));
        var remote = StormChaseAtmosphere.Sample(73019, 2, 16.2f, new(-15000, 0, -14200));
        Check("regional profiles vary across the actual world", remote.Diagnostics != replay.Diagnostics);
        var midnightBefore = StormChaseAtmosphere.Sample(45, 2, 23.999f, location);
        var midnightAfter = StormChaseAtmosphere.Sample(45, 3, .001f, location);
        Check("surface thermodynamics remain continuous through midnight", Math.Abs(midnightBefore.Diagnostics.SurfaceTemperatureCelsius - midnightAfter.Diagnostics.SurfaceTemperatureCelsius) < .01
            && Math.Abs(midnightBefore.Diagnostics.DewPointCelsius - midnightAfter.Diagnostics.DewPointCelsius) < .01);
        Check("hodograph remains continuous through midnight", Math.Abs(midnightBefore.Diagnostics.StormMotion.X - midnightAfter.Diagnostics.StormMotion.X) < .01
            && Math.Abs(midnightBefore.Diagnostics.StormMotion.Z - midnightAfter.Diagnostics.StormMotion.Z) < .01);
        {
            // Fronts move continuously through midnight: sample next to each day's boundary (where a
            // jump in its position would flip the air mass) for many seeds/days, not one fixed point.
            int jumps = 0, samples = 0;
            for (int seed = 1; seed <= 32; seed++)
            for (int day = 1; day <= 4; day++)
            {
                var boundary = StormChaseAtmosphere.Boundary(seed, day, 15);
                foreach (var (along, into) in new[] { (0f, 2000f), (0f, -2000f), (8000f, 6000f), (-8000f, -6000f) })
                {
                    var (x, z) = boundary.Point(along, into);
                    var before = StormChaseAtmosphere.Sample(seed, day, 23.999f, new(x, 0, z)).Diagnostics;
                    var after = StormChaseAtmosphere.Sample(seed, day + 1, .001f, new(x, 0, z)).Diagnostics;
                    samples++;
                    if (Math.Abs(before.SurfaceTemperatureCelsius - after.SurfaceTemperatureCelsius) > .05f
                        || Math.Abs(before.DewPointCelsius - after.DewPointCelsius) > .05f
                        || Math.Abs(before.CapeJkg - after.CapeJkg) > 15) jumps++;
                }
            }
            Console.WriteLine($"Midnight boundary continuity: {jumps}/{samples} near-boundary samples jumped.");
            Check("boundary air masses remain continuous through midnight near fronts and drylines", jumps == 0);
        }
        {
            // Curved boundaries and triple points.
            int days = 0, candidates = 0, withTriple = 0, tripleInPlay = 0, tripleOnBoth = 0, distanceOk = 0, distanceSamples = 0,
                curvedDrylines = 0, drylines = 0, wedgeOk = 0, favoured = 0, compared = 0, weakerCap = 0, moreSrh = 0, moreInitiation = 0;
            float maxSlope = 0;
            for (int seed = 1; seed <= 64; seed++)
            for (int day = 1; day <= 4; day++)
            {
                var b = StormChaseAtmosphere.Boundary(seed, day, 15); days++;
                // The perpendicular-distance approximation holds on the curve (3 km in should read ~3 km).
                float minimum = float.MaxValue, maximum = float.MinValue;
                for (int a = -20; a <= 20; a += 4)
                {
                    var (x, z) = b.Point(a * 1000, 3000);
                    float d = b.PrimaryDistance(x, z); distanceSamples++;
                    if (d is > 2500 and < 3050) distanceOk++;
                    var (bx, bz) = b.Point(a * 1000, 0); float along = -bx * b.NormalZ + bz * b.NormalX;
                    float normal = bx * b.NormalX + bz * b.NormalZ - b.Offset;
                    minimum = Math.Min(minimum, normal); maximum = Math.Max(maximum, normal);
                    var (cx2, cz2) = b.Point(a * 1000 + 100, 0);
                    maxSlope = Math.Max(maxSlope, MathF.Abs((cx2 * b.NormalX + cz2 * b.NormalZ) - (bx * b.NormalX + bz * b.NormalZ)) / 100);
                }
                if (b.Kind == "Dryline") { drylines++; if (maximum - minimum > 1500) curvedDrylines++; }
                if (b.Kind == "Warm front") continue;
                candidates++;
                if (!b.HasSecondary) continue;
                withTriple++;
                if (!b.TryTriplePoint(out float tx, out float tz)) continue;
                if (MathF.Abs(tx) < 24500 && MathF.Abs(tz - StormChaseWorld.PlayableCenterZ) < 24500) tripleInPlay++;
                if (MathF.Abs(b.PrimaryDistance(tx, tz)) < 60 && MathF.Abs(b.SecondaryDistance(tx, tz)) < 60) tripleOnBoth++;
                var (wx, wz) = b.WarmSectorDirection();
                // Inside the wedge both lines are behind you; past the warm front you are on its cool side.
                if (b.SignedDistance(tx + wx * 4000, tz + wz * 4000) > 1000 && b.SecondaryDistance(tx - b.SecondaryNormalX * 4000, tz - b.SecondaryNormalZ * 4000) < 0
                    && b.SignedDistance(tx - b.SecondaryNormalX * 4000, tz - b.SecondaryNormalZ * 4000) < 0
                    && b.NearestKind(tx - b.SecondaryNormalX * 4000, tz - b.SecondaryNormalZ * 4000) == StormChaseBoundary.SecondaryKind) wedgeOk++;
                // The triple point focuses lift: compare the same depth into the warm sector there and 18 km down the main boundary.
                var near = StormChaseAtmosphere.Sample(seed, day, 15, new(tx + wx * 3500, 0, tz + wz * 3500)).Diagnostics;
                float tripleAlong = -tx * b.NormalZ + tz * b.NormalX;
                var (fx, fz) = b.Point(tripleAlong + 18000 * MathF.Sign(b.SecondaryDistance(tx - b.NormalZ * 1000, tz + b.NormalX * 1000) - b.SecondaryDistance(tx, tz)), 3500);
                var far = StormChaseAtmosphere.Sample(seed, day, 15, new(fx, 0, fz)).Diagnostics;
                compared++;
                if (near.CinJkg >= far.CinJkg - 5 && near.Srh01KmM2S2 > far.Srh01KmM2S2) favoured++;
                if (near.CinJkg >= far.CinJkg - 5) weakerCap++;
                if (near.Srh01KmM2S2 > far.Srh01KmM2S2) moreSrh++;
                if (near.InitiationPotential01 > far.InitiationPotential01) moreInitiation++;
            }
            Console.WriteLine($"Boundaries: {days} days; curved drylines {curvedDrylines}/{drylines}; max slope {maxSlope:0.00}; curve distance ok {distanceOk}/{distanceSamples}; " +
                $"triple points {withTriple}/{candidates} (in play {tripleInPlay}, on both lines {tripleOnBoth}, wedge {wedgeOk}); triple point favoured {favoured}/{compared} (weaker cap {weakerCap}, more SRH {moreSrh}, more initiation {moreInitiation}).");
            Check("boundaries curve gently without folding (drylines bulge > 1.5 km; slope < 0.4)", curvedDrylines > drylines * .7f && maxSlope < .4f);
            Check("curved boundary distance stays a true perpendicular distance", distanceOk >= distanceSamples * .95f);
            Check("a warm front and triple point appear on roughly half of dryline/cold-front days", withTriple > candidates * .3f && withTriple < candidates * .6f);
            Check("triple points sit on both boundaries, mostly inside the playable area", tripleOnBoth == withTriple && tripleInPlay >= withTriple * .8f);
            Check("the warm sector is the wedge on the warm side of both lines", wedgeOk == withTriple);
            Check("the triple point focuses backed winds without a stronger cap than down the boundary", favoured >= compared * .8f);
        }
        {
            int higher = 0, total = 0; float sumNear = 0, sumFar = 0;
            for (int seed = 1; seed <= 400 && total < 40; seed++)
            {
                var b = StormChaseAtmosphere.Boundary(seed, 1, 15);
                if (b.Kind != "Warm front") continue;
                var (nx1, nz1) = b.Point(0, 1500); var (fx1, fz1) = b.Point(0, 30000);
                var n1 = StormChaseAtmosphere.Sample(seed, 1, 15, new(nx1, 0, nz1)).Diagnostics;
                var f1 = StormChaseAtmosphere.Sample(seed, 1, 15, new(fx1, 0, fz1)).Diagnostics;
                total++; if (n1.Srh01KmM2S2 > f1.Srh01KmM2S2) higher++; sumNear += n1.Srh01KmM2S2; sumFar += f1.Srh01KmM2S2;
            }
            Console.WriteLine($"Warm-front helicity: higher at the front on {higher}/{total} days; mean 0-1 km SRH {sumNear / total:0} at 1.5 km vs {sumFar / total:0} at 30 km.");
            Check("warm-front backing turns the low-level hodograph and raises 0-1 km helicity near the front", higher >= total * .85f && sumNear > sumFar);
        }
        // The game's outlook follows each day's boundary target; a fixed town can sit on the cool side for days.
        var outlook = StormChaseAtmosphere.CreateThreeDayForecast(73019, 2);
        Check("outlook spans three independent complete campaign days", outlook.Length == 3 && outlook.Select(x => x.DayNumber).SequenceEqual([2, 3, 4])
            && outlook.Select(x => x.Peak.CapeJkg).Distinct().Count() > 1);
        var snapshot = StormChaseAtmosphere.Snapshot(73019, 2, 16.2f, 1234, 24680, location);
        var json = JsonSerializer.Serialize(snapshot);
        var restored = JsonSerializer.Deserialize<AtmosphereSnapshot>(json)!;
        Check("immutable atmosphere protocol round trips", restored.Diagnostics == snapshot.Diagnostics
            && restored.Sounding.SequenceEqual(snapshot.Sounding) && restored.ThreeDayForecast.SequenceEqual(snapshot.ThreeDayForecast));
        Check("sample clock and world location are authoritative metadata", restored.ServerTick == 24680 && restored.CampaignSeconds == 1234 && restored.SamplePosition == location);

        var distribution = new List<object>();
        int quiet = 0, severe = 0, supportedNight = 0, stabilizedNight = 0, stronglyCurved = 0, modestRotation = 0;
        float maximumSrh01 = 0, maximumStp = 0;
        bool finite = true; int nightExceptions = 0, dawnExceptions = 0;
        var stopwatch = Stopwatch.StartNew();
        for (int seed = 1; seed <= 96; seed++)
        {
            var afternoon = StormChaseAtmosphere.Sample(seed, 1, 15, location).Diagnostics;
            var night = StormChaseAtmosphere.Sample(seed, 1, 3, location).Diagnostics;
            var earlyMorning = StormChaseAtmosphere.Sample(seed, 1, 6, location).Diagnostics;
            if (afternoon.CapeJkg < 350 || afternoon.InitiationPotential01 < .1f) quiet++;
            if (afternoon.CapeJkg > 1200 && afternoon.Stp > .4f && afternoon.InitiationPotential01 > .2f) severe++;
            if (night.CapeJkg > 250 && night.InitiationPotential01 > .02f && night.Stp > .15f) supportedNight++;
            if (night.CapeJkg < 100 || night.InitiationPotential01 < .01f) stabilizedNight++;
            if (afternoon.Srh01KmM2S2 > 200) stronglyCurved++;
            if (afternoon.Srh01KmM2S2 < 40) modestRotation++;
            maximumSrh01 = Math.Max(maximumSrh01, afternoon.Srh01KmM2S2); maximumStp = Math.Max(maximumStp, afternoon.Stp);
            foreach (var diagnostic in new[] { afternoon, night, earlyMorning })
                finite &= typeof(AtmosphereDiagnostics).GetProperties().Where(p => p.PropertyType == typeof(float))
                    .Select(p => (float)p.GetValue(diagnostic)!).All(float.IsFinite)
                    && diagnostic.CapeJkg >= 0 && diagnostic.CinJkg <= 0 && diagnostic.StormMotion.IsFinite;
            // A passing dryline, front or warm-front retreat can legitimately moisten or warm a
            // fixed point overnight; the diurnal trend must hold for the clear majority of seeds.
            if (night.CapeJkg > afternoon.CapeJkg + 10) nightExceptions++;
            if (earlyMorning.SurfaceTemperatureCelsius > night.SurfaceTemperatureCelsius + .01f
                || earlyMorning.CapeJkg > night.CapeJkg + 10) dawnExceptions++;
            distribution.Add(new { seed, afternoon, night, earlyMorning });
        }
        stopwatch.Stop();
        Check("96-seed profiles and composites remain finite with correct energy signs", finite);
        Check("96 seeds include quiet and usable severe daytime environments", quiet >= 10 && severe >= 10);
        Check("retained moisture/shear allows some usable nighttime environments", supportedNight >= 1 && supportedNight < 50);
        Check("most nights stabilize without forced sunset deletion", stabilizedNight > 48);
        Console.WriteLine($"Boundary-passage exceptions: night energy {nightExceptions}/96, dawn cooling {dawnExceptions}/96.");
        Check("nighttime parcel energy generally declines", nightExceptions <= 9);
        Check("surface cooling continues through late night toward dawn", dawnExceptions <= 9);
        Check("actual hodographs include rare strong curvature and many modest rotation environments", stronglyCurved is > 0 and < 20 && modestRotation > 30);
        // Backed surface winds now genuinely turn the low-level hodograph; extremes must stay within
        // what major observed outbreak environments reach (0-1 km SRH ~600-800 m2/s2, STP ~10).
        Check("extreme helicity and STP stay within observed outbreak ranges", maximumSrh01 is > 300 and < 900 && maximumStp is > 2 and < 12);
        var current = StormChaseAtmosphere.Snapshot(73019, 1, 15, 350, 7000, location);
        File.WriteAllText(Path.Combine(output, "atmosphere-check.json"), JsonSerializer.Serialize(new
        {
            allChecksPassed = failures == 0, checkCount = checks.Count, checks,
            sampling = new { columns = 288, elapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds,
                millisecondsPerColumn = stopwatch.Elapsed.TotalMilliseconds / 288 },
            histogram = new { quiet, severe, supportedNight, stabilizedNight, stronglyCurved, modestRotation, maximumSrh01, maximumStp },
            independentFixtures = new { stable = stable.Diagnostics, unstable = unstable.Diagnostics,
                capped = capped.Diagnostics, dry = dry.Diagnostics, curved = curved.Diagnostics },
            defaultCampaign = current, distribution,
            sources = new[] {
                "https://www.weather.gov/source/zhu/ZHU_Training_Page/convective_parameters/Sounding_Stuff/MesoscaleParameters.html",
                "https://unidata.github.io/MetPy/latest/api/generated/metpy.calc.moist_lapse.html",
                "https://unidata.github.io/MetPy/latest/api/generated/metpy.calc.significant_tornado.html",
                "https://unidata.github.io/MetPy/latest/api/generated/metpy.calc.bunkers_storm_motion.html",
                "https://unidata.github.io/MetPy/latest/api/generated/metpy.calc.storm_relative_helicity.html"
            }
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Atmosphere checks: {checks.Count - failures}/{checks.Count}. Quiet {quiet}; severe {severe}; supported nights {supportedNight}; stable nights {stabilizedNight}.");
        return failures == 0 ? 0 : 1;
    }
}
