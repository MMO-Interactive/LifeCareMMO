using System.Collections.Immutable;
using StormChase.Protocol;

/// <summary>
/// A reduced, deterministic atmosphere for a chase game, not numerical weather prediction.
/// Synoptic ingredients shape environmental temperature, moisture and vector wind profiles;
/// displayed CAPE, CIN, shear and helicity are then diagnosed from those profiles.
/// Sampling belongs in the caller's coarse weather cache, never in the render/physics hot path.
/// </summary>
internal static class StormChaseAtmosphere
{
    private const double Gravity = 9.80665, Rd = 287.05, Cp = 1004.0, Epsilon = .622, Lv = 2_500_000;
    private const float KnotsToMps = .51444445f;
    private const int VerticalStepMeters = 125, TopMeters = 12000;

    public static AtmosphereColumn Sample(int campaignSeed, int dayNumber, float hour, NetVector3 position)
    {
        if (dayNumber < 1 || dayNumber > 100000 || !float.IsFinite(hour) || hour is < 0 or >= 24 || !position.IsFinite)
            throw new ArgumentOutOfRangeException(nameof(dayNumber), "A finite campaign location and day/hour are required.");
        // Each synoptic day's anchor is 15:00. Smoothly blending the adjacent
        // anchors prevents an arbitrary profile discontinuity at midnight.
        double dayPosition = dayNumber - 1 + (hour - 15d) / 24;
        int lowerDay = Math.Max(1, (int)Math.Floor(dayPosition) + 1);
        float fraction = dayPosition < 0 ? 0 : Smooth((float)(dayPosition - Math.Floor(dayPosition)));
        var left = StormChaseCampaignWeather.CreateForecast(campaignSeed, lowerDay);
        var right = StormChaseCampaignWeather.CreateForecast(campaignSeed, lowerDay + 1);
        double seedPhase = (uint)campaignSeed * .000013714;
        float spatial = (float)(.62 * Math.Sin(position.X / 9500d + seedPhase)
            + .38 * Math.Cos(position.Z / 11700d - seedPhase * .7));
        float boundary = (float)Math.Sin(position.X / 17000d - position.Z / 13500d + seedPhase * .3);
        float targetEnergy = Lerp(left.InstabilityJkg, right.InstabilityJkg, fraction);
        float dew = Lerp(left.DewPointCelsius, right.DewPointCelsius, fraction) + spatial * 1.5f;
        float cap = Math.Clamp(Lerp(left.CapStrength, right.CapStrength, fraction) + boundary * .1f, 0, 1);
        // Mesoscale boundary of each synoptic day (dryline / cold front / warm front), blended like the day anchors.
        // Each boundary keeps its own clock (hours since its day's midnight), so the earlier day's
        // front is at 26:00 rather than 02:00 after midnight and never jumps back.
        float leftHour = BoundaryHour(dayNumber, hour, lowerDay);
        var effectLeft = BoundaryEffect(campaignSeed, lowerDay, leftHour, position.X, position.Z);
        var effectRight = BoundaryEffect(campaignSeed, lowerDay + 1, leftHour - 24, position.X, position.Z);
        dew += Lerp(effectLeft.Dew, effectRight.Dew, fraction);
        cap = Math.Clamp(cap * Lerp(effectLeft.CapFactor, effectRight.CapFactor, fraction), 0, 1);
        // Gulf moisture deepens toward the south-east coast. On warm afternoons the sea-breeze front
        // pushes cool, moist marine air inland; convergence along its leading edge erodes the cap,
        // while the marine air behind it is slightly more stable.
        dew += StormChaseCoast.GulfMoistureDew(position.X, position.Z);
        var breeze = StormChaseCoast.SeaBreeze(campaignSeed, dayNumber, hour);
        float breezeDistance = breeze.SignedDistance(position.X, position.Z);
        float marine = breeze.Strength * Smooth(Math.Clamp((breezeDistance + 400) / 2400, 0, 1));
        float breezeLift = breeze.Strength * MathF.Exp(-((breezeDistance + 300) / 2200) * ((breezeDistance + 300) / 2200));
        dew += 1.2f * marine;
        cap = Math.Clamp(cap * (1 - .5f * breezeLift) * (1 + .2f * marine), 0, 1);
        float nocturnalSupport = Lerp(left.NocturnalSupport, right.NocturnalSupport, fraction);
        float shear = Lerp(left.ShearKnots, right.ShearKnots, fraction) * KnotsToMps * (1 + boundary * .12f);
        float heading = Lerp(left.TrackDirectionDegrees, right.TrackDirectionDegrees, fraction) * MathF.PI / 180;
        // Smooth asymmetric daily temperature cycle: warming from 06:00 to
        // 15:00, then gradual cooling to the next dawn. There is no midnight reset.
        float heat = hour is >= 6 and < 15
            ? MathF.Pow(MathF.Sin((hour - 6) * MathF.PI / 18), 1.6f)
            : .5f + .5f * MathF.Cos((hour >= 15 ? hour - 15 : hour + 9) * MathF.PI / 15);
        float night = 1 - heat;
        float peakTemperature = dew + 5.7f + targetEnergy / 1050;
        float surfaceTemperature = peakTemperature - night * (8.5f - 4.5f * nocturnalSupport) + spatial * .7f
            + Lerp(effectLeft.Temperature, effectRight.Temperature, fraction) * (.35f + .65f * heat) - 2.2f * marine;
        float surfaceDew = Math.Min(surfaceTemperature - .2f, dew + night * .45f);
        // Moist active days retain a warm boundary layer while supported shear
        // strengthens near 1 km overnight; weaker days develop a stable surface inversion.
        float jet = night * nocturnalSupport * (5 + shear * .22f);
        var leftWinds = HodographShape(left); var rightWinds = HodographShape(right);
        float lowCurve = Lerp(leftWinds.CurveMetersPerSecond, rightWinds.CurveMetersPerSecond, fraction);
        float curveDepth = Lerp(leftWinds.CurveDepthKm, rightWinds.CurveDepthKm, fraction);
        float lowShearFraction = Lerp(leftWinds.LowShearFraction, rightWinds.LowShearFraction, fraction);
        float backing = Lerp(leftWinds.BackingMetersPerSecond, rightWinds.BackingMetersPerSecond, fraction);
        // Backing (from the day's wind pattern and from boundaries) is a surface effect: friction and
        // pressure troughs turn the near-surface wind. It fades over the lowest ~1 km, which turns the
        // low-level hodograph and raises 0-1 km helicity. Applied uniformly with height it would only
        // translate the hodograph and leave shear and helicity unchanged.
        float boundaryBacking = Lerp(effectLeft.Backing, effectRight.Backing, fraction);
        float midLapse = 5.6f + Math.Clamp(targetEnergy / 4500, 0, 1) * 1.6f;
        float boundaryLapse = 6.8f + Math.Clamp(targetEnergy / 4500, 0, 1) * 1.1f;
        var levels = ImmutableArray.CreateBuilder<AtmosphereLevel>(TopMeters / VerticalStepMeters + 1);
        double pressure = 990 + 7 * boundary;
        float previousTemperature = surfaceTemperature, previousDew = surfaceDew;
        float cosine = MathF.Cos(heading), sine = MathF.Sin(heading);
        for (int height = 0; height <= TopMeters; height += VerticalStepMeters)
        {
            float zKm = height / 1000f;
            float temperature = peakTemperature - boundaryLapse * Math.Min(zKm, 2)
                - midLapse * Math.Clamp(zKm - 2, 0, 7)
                + 1.0f * Math.Max(0, zKm - 9);
            temperature += (surfaceTemperature - peakTemperature) * MathF.Exp(-height / 900f);
            // Elevated mixed layer: a narrow warm nose, rather than a scalar
            // fake CIN value. It can prevent convection despite positive CAPE aloft.
            temperature += cap * 7.5f * (MathF.Exp(-MathF.Pow((height - 1250) / 500f, 2))
                - MathF.Exp(-MathF.Pow(1250 / 500f, 2)) * MathF.Exp(-height / 600f));
            float layerDew = Math.Min(temperature - .1f, surfaceDew - 1.8f * Math.Min(zKm, 1.5f)
                - 5.6f * Math.Max(0, zKm - 1.5f));
            if (height > 0)
            {
                double tMean = (previousTemperature + temperature) * .5 + 273.15;
                double dewMean = (previousDew + layerDew) * .5;
                double mixing = MixingRatio(SaturationVaporPressure(dewMean), pressure);
                pressure *= Math.Exp(-Gravity * VerticalStepMeters / (Rd * VirtualTemperature(tMean, mixing)));
            }
            float progress = Math.Clamp(zKm / 6, 0, 1);
            float surfaceBacking = (backing + boundaryBacking) * MathF.Exp(-zKm / .7f);
            float east = 3 - surfaceBacking + shear * (lowShearFraction * Math.Clamp(zKm, 0, 1) + (1 - lowShearFraction) * progress);
            float north = 4 + surfaceBacking * .6f - 2 * progress
                + lowCurve * MathF.Sin(MathF.PI * Math.Clamp(zKm / curveDepth, 0, 1))
                + jet * MathF.Exp(-MathF.Pow((zKm - .9f) / .65f, 2));
            if (zKm > 6) east += shear * .07f * (zKm - 6);
            float u = east * cosine - north * sine, v = east * sine + north * cosine;
            levels.Add(new(height, (float)pressure, temperature, layerDew, u, v));
            previousTemperature = temperature; previousDew = layerDew;
        }
        return Diagnose(levels.MoveToImmutable());
    }

    public static AtmosphereSnapshot Snapshot(int campaignSeed, int dayNumber, float hour,
        double campaignSeconds, long serverTick, NetVector3 samplePosition, bool includeForecast = true)
    {
        var column = Sample(campaignSeed, dayNumber, hour, samplePosition);
        return new(serverTick, campaignSeconds, dayNumber, hour, samplePosition, column.Levels,
            column.Diagnostics, includeForecast ? CreateThreeDayForecast(campaignSeed, dayNumber, samplePosition) : []);
    }

    public static ImmutableArray<AtmosphereDayForecast> CreateThreeDayForecast(int campaignSeed,
        int dayNumber, NetVector3 position)
    {
        var result = ImmutableArray.CreateBuilder<AtmosphereDayForecast>(3);
        for (int day = dayNumber; day < dayNumber + 3; day++) result.Add(DayForecast(campaignSeed, day, position, position));
        return result.MoveToImmutable();
    }

    /// <summary>
    /// The day's forecast target: like a human forecaster, look along the moist side of the day's
    /// boundary (3–6 km in, where convergence weakens the cap) inside the playable area and take the
    /// point with the best CAPE × initiation potential at <paramref name="hour"/> (hours since the day's
    /// midnight, so 26 is 02:00 the following night). Deterministic; ~21 columns.
    /// </summary>
    public static NetVector3 OutlookPoint(int campaignSeed, int dayNumber, float hour = 15)
    {
        var boundary = Boundary(campaignSeed, dayNumber, hour);
        int sampleDay = dayNumber + (int)MathF.Floor(hour / 24); float sampleHour = hour - (sampleDay - dayNumber) * 24;
        const float Limit = StormChaseWorld.PlayableExtentMeters * .5f - 2500;
        NetVector3 best = new(6000, 0, 7000); float bestScore = float.NegativeInfinity;
        for (int a = -6; a <= 6; a += 2)
        for (int i = 0; i < 3; i++)
        {
            var (x, z) = boundary.Point(a * 3500, 3000 + i * 1500);
            if (MathF.Abs(x) > Limit || MathF.Abs(z - StormChaseWorld.PlayableCenterZ) > Limit) continue;
            var d = Sample(campaignSeed, sampleDay, sampleHour, new(x, 0, z)).Diagnostics;
            float energy = Math.Clamp(d.CapeJkg / 2500, 0, 1);
            float score = d.InitiationPotential01 * Math.Max(.05f, energy) + .001f * energy;   // CAPE breaks capped-day ties
            if (score > bestScore) { bestScore = score; best = new(MathF.Round(x), 0, MathF.Round(z)); }
        }
        // The triple point is the classic target: also look 2.5-5.5 km into the warm sector from it.
        if (boundary.TryTriplePoint(out float tx, out float tz))
        {
            var (dx, dz) = boundary.WarmSectorDirection();
            for (int i = 0; i < 3; i++)
            {
                float x = tx + dx * (2500 + 1500 * i), z = tz + dz * (2500 + 1500 * i);
                if (MathF.Abs(x) > Limit || MathF.Abs(z - StormChaseWorld.PlayableCenterZ) > Limit) continue;
                var d = Sample(campaignSeed, sampleDay, sampleHour, new(x, 0, z)).Diagnostics;
                float energy = Math.Clamp(d.CapeJkg / 2500, 0, 1);
                float score = d.InitiationPotential01 * Math.Max(.05f, energy) + .001f * energy;
                if (score > bestScore) { bestScore = score; best = new(MathF.Round(x), 0, MathF.Round(z)); }
            }
        }
        return best;
    }

    /// <summary>
    /// Three-day outlook where each day's peak (15:00) and overnight (02:00) values come from that time's
    /// target area: by night a cold front has moved on, so the nocturnal target is re-picked ahead of it.
    /// </summary>
    public static ImmutableArray<AtmosphereDayForecast> CreateThreeDayForecast(int campaignSeed, int dayNumber)
    {
        var result = ImmutableArray.CreateBuilder<AtmosphereDayForecast>(3);
        for (int day = dayNumber; day < dayNumber + 3; day++)
            result.Add(DayForecast(campaignSeed, day, OutlookPoint(campaignSeed, day), OutlookPoint(campaignSeed, day, 26)));
        return result.MoveToImmutable();
    }

    /// <summary>Server/client-identical snapshot for the day's outlook target (current sounding at the 15:00 target point).</summary>
    public static AtmosphereSnapshot OutlookSnapshot(int campaignSeed, int dayNumber, float hour,
        double campaignSeconds, long serverTick, ImmutableArray<AtmosphereDayForecast>? forecast = null)
        => Snapshot(campaignSeed, dayNumber, hour, campaignSeconds, serverTick, OutlookPoint(campaignSeed, dayNumber), false)
            with { ThreeDayForecast = forecast ?? CreateThreeDayForecast(campaignSeed, dayNumber) };

    private static AtmosphereDayForecast DayForecast(int campaignSeed, int day, NetVector3 peakPosition, NetVector3 nightPosition)
    {
        var peak = Sample(campaignSeed, day, 15, peakPosition).Diagnostics;
        var overnight = Sample(campaignSeed, day + 1, 2, nightPosition).Diagnostics;
        float risk = Math.Clamp(peak.InitiationPotential01 * .35f + peak.TornadoPotential01 * .65f, 0, 1);
        string name = risk < .15f ? "Quiet / capped" : risk < .3f ? "Marginal" : risk < .45f ? "Slight"
            : risk < .6f ? "Enhanced" : risk < .75f ? "Moderate" : "High";
        return new(day, name, risk, peak, overnight);
    }

    private static HodographParameters HodographShape(StormChaseDayForecast day)
    {
        uint seed = unchecked((uint)day.Seed ^ 0x5f3759dfu);
        seed = unchecked((seed ^ (seed >> 16)) * 0x7feb352du);
        seed = unchecked((seed ^ (seed >> 15)) * 0x846ca68bu);
        float roll = (seed & 0x00ffffff) / 16777216f;
        float shear = day.ShearKnots * KnotsToMps;
        // Wind profiles themselves vary. Most environments have modest curvature;
        // a minority of moist, strongly sheared air masses have backed surface
        // winds, substantial shear below 1 km, and a tighter veering hodograph.
        // No diagnostic or tornado intensity is assigned by selecting this mode.
        if (shear >= 16 && day.DewPointCelsius >= 17 && roll > .79f)
            return new(15 + shear * .48f, 2.0f + roll * .45f, .34f + (roll - .79f) * .7f, 4 + roll * 3);
        if (roll < .27f || shear < 11)
            return new(1.5f + shear * .08f, 3.5f + roll, .02f, 0);
        return new(3 + shear * (.2f + .18f * day.NocturnalSupport), 2.8f + roll * .8f, .08f + roll * .07f, 0);
    }

    /// <summary>
    /// Diagnose an independently supplied sounding, also used by analytic regression fixtures.
    /// Hydrostatic pressure/profile generation is separate from parcel integration.
    /// </summary>
    public static AtmosphereColumn Diagnose(ImmutableArray<AtmosphereLevel> input)
    {
        Validate(input);
        double surfaceT = input[0].TemperatureCelsius + 273.15;
        double surfaceTd = input[0].DewPointCelsius + 273.15;
        double surfaceP = input[0].PressureHpa;
        double initialMixing = MixingRatio(SaturationVaporPressure(input[0].DewPointCelsius), surfaceP);
        // Bolton (1980) liquid-water LCL temperature approximation, with Kelvin inputs.
        // https://doi.org/10.1175/1520-0493(1980)108<1046:TCOTEP>2.0.CO;2
        double lclT = 1 / (1 / (surfaceTd - 56) + Math.Log(surfaceT / surfaceTd) / 800) + 56;
        double lclP = surfaceP * Math.Pow(lclT / surfaceT, Cp / Rd);
        float lcl = HeightAtPressure(input, lclP);
        var levels = ImmutableArray.CreateBuilder<AtmosphereLevel>(input.Length);
        double[] buoyancy = new double[input.Length], dryBuoyancy = new double[input.Length];
        double parcelT = surfaceT, priorP = surfaceP;
        for (int index = 0; index < input.Length; index++)
        {
            var level = input[index];
            double p = level.PressureHpa;
            if (p >= lclP) parcelT = surfaceT * Math.Pow(p / surfaceP, Rd / Cp);
            else
            {
                double topOfDry = Math.Min(priorP, lclP);
                double beginningT = priorP >= lclP ? lclT : parcelT;
                // Integrate dT/dP using midpoint substeps (at most 2 hPa).
                // This is the liquid-saturated pseudoadiabat: condensate falls out.
                // https://unidata.github.io/MetPy/latest/api/generated/metpy.calc.moist_lapse.html
                int steps = Math.Max(1, (int)Math.Ceiling((topOfDry - p) / 2));
                double delta = (p - topOfDry) / steps;
                parcelT = beginningT;
                for (int step = 0; step < steps; step++)
                {
                    double atP = topOfDry + delta * step;
                    double first = MoistDerivative(parcelT, atP);
                    parcelT += MoistDerivative(parcelT + first * delta * .5, atP + delta * .5) * delta;
                }
            }
            double envT = level.TemperatureCelsius + 273.15;
            double envMixing = MixingRatio(SaturationVaporPressure(level.DewPointCelsius), p);
            double parcelMixing = p >= lclP ? initialMixing : MixingRatio(SaturationVaporPressure(parcelT - 273.15), p);
            double envVirtual = VirtualTemperature(envT, envMixing);
            buoyancy[index] = Gravity * (VirtualTemperature(parcelT, parcelMixing) - envVirtual) / envVirtual;
            dryBuoyancy[index] = Gravity * (parcelT - envT) / envT;
            levels.Add(level with { ParcelTemperatureCelsius = (float)(parcelT - 273.15) });
            priorP = p;
        }
        var profile = levels.MoveToImmutable();
        double cape = 0, cin = 0, cape03 = 0;
        float lfc = 0, equilibrium = 0;
        // LFC is the first positive-buoyancy region at/above saturation. Interpolate
        // zero crossings so a thin cap does not depend on which grid level it lands on.
        for (int i = 1; i < profile.Length; i++)
        {
            float low = Math.Max(lcl, profile[i - 1].HeightMeters), high = profile[i].HeightMeters;
            if (high <= low) continue;
            double bLow = Interpolate(profile[i - 1].HeightMeters, profile[i].HeightMeters, buoyancy[i - 1], buoyancy[i], low);
            if (bLow > 0 || buoyancy[i] > 0)
            {
                lfc = bLow >= 0 ? low : (float)(low + (high - low) * -bLow / (buoyancy[i] - bLow));
                break;
            }
        }
        for (int i = 1; i < profile.Length; i++)
        {
            float low = profile[i - 1].HeightMeters, high = profile[i].HeightMeters;
            if (lfc > 0 && high > lfc)
            {
                float start = Math.Max(low, lfc);
                cape += PositiveArea(start, high, low, high, buoyancy[i - 1], buoyancy[i]);
                if (start < 3000) cape03 += PositiveArea(start, Math.Min(3000, high), low, high, buoyancy[i - 1], buoyancy[i]);
                if (buoyancy[i] > 0) equilibrium = high;
                else if (buoyancy[i - 1] > 0)
                    equilibrium = (float)(low + (high - low) * buoyancy[i - 1] / (buoyancy[i - 1] - buoyancy[i]));
            }
            // SPC's displayed CIN uses nonvirtual parcel/environment temperatures.
            // https://www.weather.gov/source/zhu/ZHU_Training_Page/convective_parameters/Sounding_Stuff/MesoscaleParameters.html
            // Without an LFC there is no surface-to-LFC CIN integral; return
            // zero for both energies instead of inventing a 12-km cap value.
            float end = lfc > 0 ? Math.Min(lfc, high) : 0;
            if (end > low) cin -= PositiveArea(low, end, low, high, -dryBuoyancy[i - 1], -dryBuoyancy[i]);
        }
        var surfaceWind = WindAt(profile, 0);
        var shear01 = Subtract(WindAt(profile, 1000), surfaceWind);
        var shear06 = Subtract(WindAt(profile, 6000), surfaceWind);
        var motion = BunkersRightMover(profile);
        float srh01 = Helicity(profile, motion, 1000), srh03 = Helicity(profile, motion, 3000);
        float shear6 = Magnitude(shear06);
        float stp = FixedLayerStp((float)cape, lcl, srh01, shear6);
        float lapse03 = (profile[0].TemperatureCelsius - TemperatureAt(profile, 3000)) / 3;
        // Deliberately named VtpGame: a modest STP extension reflecting diagnosed
        // low-level buoyancy/stretching and lapse rate. It is not operational VTP.
        float vtp = stp * Math.Clamp((float)cape03 / 150, 0, 2) * Math.Clamp(lapse03 / 7, 0, 1.5f);
        float inhibition = MathF.Exp((float)cin / 130);
        float initiation = Math.Clamp((float)cape / 1200, 0, 1) * inhibition;
        float rotation = Math.Clamp(shear6 / 25, 0, 1) * Math.Clamp(Math.Max(0, srh01) / 180, 0, 1);
        float tornado = Math.Clamp((1 - MathF.Exp(-stp / 2)) * .65f + rotation * initiation * .25f
            + Math.Clamp(vtp / 5, 0, 1) * .1f, 0, 1);
        return new(profile, new((float)cape, (float)cin, (float)cape03, lcl, lfc, equilibrium,
            Magnitude(shear01), shear6, srh01, srh03, motion, stp, vtp,
            profile[0].TemperatureCelsius, profile[0].DewPointCelsius, lapse03, initiation, tornado));
    }

    public static float FixedLayerStp(float cape, float lcl, float srh01, float shear06)
    {
        // SPC fixed-layer formula: surface CAPE/1500, 0–1 km SRH/150,
        // 0–6 km shear/20 (zero below 12.5 m/s, max 1.5), LCL term [0,1].
        // https://unidata.github.io/MetPy/latest/api/generated/metpy.calc.significant_tornado.html
        float shearTerm = shear06 < 12.5f ? 0 : Math.Min(shear06 / 20, 1.5f);
        return Math.Max(0, cape) / 1500 * Math.Max(0, srh01) / 150 * shearTerm * Math.Clamp((2000 - lcl) / 1000, 0, 1);
    }

    public static NetVector3 BunkersRightMover(ImmutableArray<AtmosphereLevel> levels)
    {
        // Bunkers et al. (2000): height mean 0–6 km plus a 7.5 m/s deviation
        // to the right of the 0–0.5 km to 5.5–6 km mean shear vector.
        // https://unidata.github.io/MetPy/latest/api/generated/metpy.calc.bunkers_storm_motion.html
        var mean = MeanWind(levels, 0, 6000);
        var shear = Subtract(MeanWind(levels, 5500, 6000), MeanWind(levels, 0, 500));
        float magnitude = Magnitude(shear);
        return magnitude < .001f ? mean : new(mean.X + 7.5f * shear.Z / magnitude, 0, mean.Z - 7.5f * shear.X / magnitude);
    }

    public static float Helicity(ImmutableArray<AtmosphereLevel> levels, NetVector3 motion, float depthMeters)
    {
        double total = 0;
        for (int i = 1; i < levels.Length && levels[i - 1].HeightMeters < depthMeters; i++)
        {
            var first = WindAt(levels, levels[i - 1].HeightMeters);
            var last = WindAt(levels, Math.Min(depthMeters, levels[i].HeightMeters));
            total += (last.X - motion.X) * (first.Z - motion.Z) - (first.X - motion.X) * (last.Z - motion.Z);
        }
        return (float)total;
    }

    private static double MoistDerivative(double temperatureKelvin, double pressureHpa)
    {
        double saturated = MixingRatio(SaturationVaporPressure(temperatureKelvin - 273.15), pressureHpa);
        return (Rd * temperatureKelvin + Lv * saturated)
            / (pressureHpa * (Cp + Lv * Lv * saturated * Epsilon / (Rd * temperatureKelvin * temperatureKelvin)));
    }

    private static double SaturationVaporPressure(double temperatureCelsius)
        => 6.112 * Math.Exp(17.67 * temperatureCelsius / (temperatureCelsius + 243.5));
    private static double MixingRatio(double vaporPressureHpa, double pressureHpa)
        => Epsilon * vaporPressureHpa / Math.Max(.01, pressureHpa - vaporPressureHpa);
    private static double VirtualTemperature(double temperatureKelvin, double mixingRatio)
        => temperatureKelvin * (1 + mixingRatio / Epsilon) / (1 + mixingRatio);

    private static double PositiveArea(float start, float end, float low, float high, double bLow, double bHigh)
    {
        if (end <= start) return 0;
        double first = Interpolate(low, high, bLow, bHigh, start), last = Interpolate(low, high, bLow, bHigh, end);
        if (first >= 0 && last >= 0) return (first + last) * .5 * (end - start);
        if (first <= 0 && last <= 0) return 0;
        double crossing = start + (end - start) * -first / (last - first);
        return first > 0 ? first * .5 * (crossing - start) : last * .5 * (end - crossing);
    }

    private static float HeightAtPressure(ImmutableArray<AtmosphereLevel> levels, double pressure)
    {
        for (int i = 1; i < levels.Length; i++)
            if (levels[i].PressureHpa <= pressure)
            {
                double fraction = (Math.Log(levels[i - 1].PressureHpa) - Math.Log(pressure))
                    / (Math.Log(levels[i - 1].PressureHpa) - Math.Log(levels[i].PressureHpa));
                return Lerp(levels[i - 1].HeightMeters, levels[i].HeightMeters, (float)Math.Clamp(fraction, 0, 1));
            }
        return levels[^1].HeightMeters;
    }

    private static NetVector3 MeanWind(ImmutableArray<AtmosphereLevel> levels, float bottom, float top)
    {
        double u = 0, v = 0;
        for (int i = 1; i < levels.Length; i++)
        {
            float low = Math.Max(bottom, levels[i - 1].HeightMeters), high = Math.Min(top, levels[i].HeightMeters);
            if (high <= low) continue;
            var first = WindAt(levels, low); var last = WindAt(levels, high);
            u += (first.X + last.X) * .5 * (high - low); v += (first.Z + last.Z) * .5 * (high - low);
        }
        return new((float)(u / (top - bottom)), 0, (float)(v / (top - bottom)));
    }

    private static NetVector3 WindAt(ImmutableArray<AtmosphereLevel> levels, float height)
    {
        int upper = 1;
        while (upper < levels.Length - 1 && levels[upper].HeightMeters < height) upper++;
        var low = levels[upper - 1]; var high = levels[upper];
        float t = Math.Clamp((height - low.HeightMeters) / (high.HeightMeters - low.HeightMeters), 0, 1);
        return new(Lerp(low.WindEastMetersPerSecond, high.WindEastMetersPerSecond, t), 0,
            Lerp(low.WindNorthMetersPerSecond, high.WindNorthMetersPerSecond, t));
    }

    private static float TemperatureAt(ImmutableArray<AtmosphereLevel> levels, float height)
    {
        int upper = 1;
        while (upper < levels.Length - 1 && levels[upper].HeightMeters < height) upper++;
        return (float)Interpolate(levels[upper - 1].HeightMeters, levels[upper].HeightMeters,
            levels[upper - 1].TemperatureCelsius, levels[upper].TemperatureCelsius, height);
    }

    private static void Validate(ImmutableArray<AtmosphereLevel> input)
    {
        if (input.IsDefault || input.Length < 3 || input[0].HeightMeters != 0 || input[^1].HeightMeters < 6000)
            throw new ArgumentException("A sounding must begin at 0 m AGL and extend through 6 km.", nameof(input));
        for (int i = 0; i < input.Length; i++)
        {
            var level = input[i];
            if (!float.IsFinite(level.HeightMeters) || !float.IsFinite(level.PressureHpa) || !float.IsFinite(level.TemperatureCelsius)
                || !float.IsFinite(level.DewPointCelsius) || !float.IsFinite(level.WindEastMetersPerSecond) || !float.IsFinite(level.WindNorthMetersPerSecond)
                || level.PressureHpa is <= 0 or > 1100 || level.TemperatureCelsius is < -110 or > 60
                || level.DewPointCelsius > level.TemperatureCelsius || level.DewPointCelsius < -120
                || i > 0 && (level.HeightMeters <= input[i - 1].HeightMeters || level.PressureHpa >= input[i - 1].PressureHpa))
                throw new ArgumentException("Sounding levels require finite, ordered heights/pressures and unsaturated environmental moisture.", nameof(input));
        }
    }

    private static double Interpolate(float low, float high, double first, double last, float at)
        => first + (last - first) * Math.Clamp((at - low) / (high - low), 0, 1);
    private static float Magnitude(NetVector3 value) => MathF.Sqrt(value.X * value.X + value.Z * value.Z);
    private static NetVector3 Subtract(NetVector3 first, NetVector3 last) => new(first.X - last.X, 0, first.Z - last.Z);
    private static float Lerp(float first, float last, float t) => first + (last - first) * t;

    /// <summary>
    /// The day's mesoscale boundary. Storms in the real Plains initiate where boundary
    /// convergence erodes the cap, not uniformly across a capped warm sector:
    /// a dryline (moist air east) mixes eastward through the afternoon and retreats overnight;
    /// a cold front (warm sector south-east) advances steadily; a warm front (warm sector south)
    /// lifts slowly north and backs the low-level wind near it, raising helicity.
    /// </summary>
    public static StormChaseBoundary Boundary(int campaignSeed, int dayNumber, float hour)
    {
        var day = StormChaseCampaignWeather.CreateForecast(campaignSeed, dayNumber);
        var random = new Random(unchecked(campaignSeed * 7919 ^ dayNumber * 104729 ^ 0x5bd1e995));
        float roll = (float)random.NextDouble();
        string kind = day.DewPointCelsius >= 16 && roll < .55f ? "Dryline" : roll < .8f ? "Cold front" : "Warm front";
        float normalDegrees = kind switch { "Dryline" => 0, "Cold front" => -45, _ => -90 }
            + ((float)random.NextDouble() - .5f) * 40;
        float angle = normalDegrees * MathF.PI / 180, nx = MathF.Cos(angle), nz = MathF.Sin(angle);
        // At 15:00 the boundary passes within ~12 km of the playable centre.
        float px = ((float)random.NextDouble() - .5f) * 24000, pz = StormChaseWorld.PlayableCenterZ + ((float)random.NextDouble() - .5f) * 24000;
        float offset = px * nx + pz * nz + BoundaryMovementMeters(kind, hour) - BoundaryMovementMeters(kind, 15);
        // Real boundaries are not straight: drylines bulge where mixing is strongest, fronts arc gently.
        // The local slope stays below ~0.35 so the warm/moist side is never folded back on itself.
        float amplitude = kind switch { "Dryline" => 2000, "Cold front" => 800, _ => 600 }
            + (float)random.NextDouble() * (kind == "Dryline" ? 1500 : 1000);
        float wavenumber = Math.Min(MathF.Tau / (26000 + 16000 * (float)random.NextDouble()), .35f / amplitude);
        var boundary = new StormChaseBoundary(kind, nx, nz, offset)
        { BendAmplitude = amplitude, BendWavenumber = wavenumber, BendPhase = (float)random.NextDouble() * MathF.Tau };
        // About half of dryline and cold-front days also carry a warm front from the surface low. Its
        // warm side is the south; where it meets the main boundary is the triple point, and the warm
        // sector is the wedge on the moist side of both.
        float secondaryRoll = (float)random.NextDouble(), secondaryAngle = (float)random.NextDouble(), tripleAlong = (float)random.NextDouble();
        if (kind == "Warm front" || secondaryRoll >= .45f) return boundary;
        float secondaryDegrees = Math.Min(-90 + (secondaryAngle - .5f) * 30, normalDegrees - 45);
        float sa = secondaryDegrees * MathF.PI / 180, snx = MathF.Cos(sa), snz = MathF.Sin(sa);
        // The triple point lies on the 15:00 main boundary, near the playable centre; each line then moves on its own.
        var noon = boundary with { Offset = px * nx + pz * nz };
        var (tx, tz) = noon.Point((tripleAlong - .5f) * 16000, 0);
        float secondaryOffset = tx * snx + tz * snz + BoundaryMovementMeters("Warm front", hour) - BoundaryMovementMeters("Warm front", 15);
        return boundary with { HasSecondary = true, SecondaryNormalX = snx, SecondaryNormalZ = snz, SecondaryOffset = secondaryOffset };
    }

    /// <summary>
    /// The boundary that dominates the atmosphere at a campaign day/hour (the same blend weights as
    /// <see cref="Sample"/>), positioned on its own continuous clock. Use this for anything shown or
    /// spawned "now"; <see cref="Boundary"/> takes a specific day and that day's relative hour.
    /// </summary>
    public static StormChaseBoundary ActiveBoundary(int campaignSeed, int dayNumber, float hour)
    {
        double dayPosition = dayNumber - 1 + (hour - 15d) / 24;
        int lowerDay = Math.Max(1, (int)Math.Floor(dayPosition) + 1);
        float fraction = dayPosition < 0 ? 0 : Smooth((float)(dayPosition - Math.Floor(dayPosition)));
        float leftHour = BoundaryHour(dayNumber, hour, lowerDay);
        return fraction < .5f ? Boundary(campaignSeed, lowerDay, leftHour) : Boundary(campaignSeed, lowerDay + 1, leftHour - 24);
    }

    /// <summary>Hours since midnight starting <paramref name="boundaryDay"/>: 26 is 02:00 the following night, -4 is 20:00 the evening before.</summary>
    private static float BoundaryHour(int dayNumber, float hour, int boundaryDay) => (dayNumber - boundaryDay) * 24 + hour;

    /// <summary>
    /// Displacement along the boundary normal (toward the moist side is positive) at hours since the
    /// boundary day's midnight; valid and continuous from the previous evening (-9) to the next afternoon (39).
    /// </summary>
    private static float BoundaryMovementMeters(string kind, float hour)
    {
        float S(float v) => Smooth(Math.Clamp(v, 0, 1));
        switch (kind)
        {
            case "Dryline":
                // Retreats west before dawn, mixes east through the afternoon, retreats again after dark.
                float cycle = hour < 9 ? .611f * (1 - S(hour / 9))
                    : hour < 18 ? S((hour - 9) / 9)
                    : 1 - .6f * S((hour - 18) / 10);
                return 16000 * cycle;
            case "Cold front":
                // Advances into the warm sector; after dark it slows as the boundary layer decouples.
                return 3500 * (Math.Min(hour, 21) - 12) + 1200 * Math.Max(0, hour - 21);
            default:
                // A warm front lifts back over the cool side by day and nearly stalls overnight.
                return -2000 * (Math.Min(hour, 21) - 12) - 500 * Math.Max(0, hour - 21);
        }
    }

    private static (float Dew, float Temperature, float CapFactor, float Backing) BoundaryEffect(int campaignSeed, int dayNumber, float hour, float x, float z)
    {
        var b = Boundary(campaignSeed, dayNumber, hour);
        float primary = b.PrimaryDistance(x, z);
        var effect = ComponentEffect(b.Kind, primary);
        if (!b.HasSecondary) return effect;
        float secondary = b.SecondaryDistance(x, z);
        var warm = ComponentEffect(StormChaseBoundary.SecondaryKind, secondary);
        float cap = Math.Min(effect.CapFactor, warm.CapFactor), backing = effect.Backing + warm.Backing;
        // The triple point: converging air from both boundaries plus the warm front's backed winds.
        if (b.TryTriplePoint(out float tx, out float tz))
        {
            float r2 = ((x - tx) * (x - tx) + (z - tz) * (z - tz)) / (7000f * 7000f);
            float focus = MathF.Exp(-r2) * Smooth(Math.Clamp((Math.Min(primary, secondary) + 2000) / 2000, 0, 1));
            cap *= 1 - .3f * focus; backing += 2.5f * focus;
        }
        return (effect.Dew + warm.Dew, effect.Temperature + warm.Temperature, cap, backing);
    }

    private static (float Dew, float Temperature, float CapFactor, float Backing) ComponentEffect(string kind, float s)
    {
        // s > 0 on the moist / warm side of this one boundary.
        float convergence = MathF.Exp(-((s - 3500) / 5500) * ((s - 3500) / 5500));
        float behind = 1 - Smooth(Math.Clamp((s + 7000) / 9000, 0, 1));     // 1 well behind the boundary
        return kind switch
        {
            "Dryline" => (-12 * behind, 2.5f * behind, 1 - .6f * convergence, 0),
            "Cold front" => (-6 * behind, -6 * behind, 1 - .55f * convergence, 0),
            _ => (-3 * behind, -5 * behind, 1 - .45f * convergence, 3.5f * MathF.Exp(-(s / 12000) * (s / 12000))),
        };
    }
    private static float Smooth(float value) => value * value * (3 - 2 * value);
    private readonly record struct HodographParameters(float CurveMetersPerSecond, float CurveDepthKm,
        float LowShearFraction, float BackingMetersPerSecond);
}

internal sealed record AtmosphereColumn(ImmutableArray<AtmosphereLevel> Levels, AtmosphereDiagnostics Diagnostics);

/// <summary>
/// A gently curved mesoscale boundary, optionally crossed by a (straight) warm front from the surface low.
/// Signed distance is positive on the moist / warm side; with a warm front, only the wedge on the warm side
/// of both lines (the warm sector) is positive. Along-boundary coordinates use t = (-NormalZ, NormalX).
/// </summary>
internal readonly record struct StormChaseBoundary(string Kind, float NormalX, float NormalZ, float Offset)
{
    public const string SecondaryKind = "Warm front";
    public float BendAmplitude { get; init; }
    public float BendWavenumber { get; init; }
    public float BendPhase { get; init; }
    public bool HasSecondary { get; init; }
    public float SecondaryNormalX { get; init; }
    public float SecondaryNormalZ { get; init; }
    public float SecondaryOffset { get; init; }

    private float Bend(float along) => BendAmplitude * MathF.Sin(BendWavenumber * along + BendPhase);
    private float Slope(float along) => BendAmplitude * BendWavenumber * MathF.Cos(BendWavenumber * along + BendPhase);

    /// <summary>Approximate perpendicular distance to the curved main boundary (exact for a straight one).</summary>
    public float PrimaryDistance(float x, float z)
    {
        float along = -x * NormalZ + z * NormalX, slope = Slope(along);
        return (x * NormalX + z * NormalZ - Offset - Bend(along)) / MathF.Sqrt(1 + slope * slope);
    }
    public float SecondaryDistance(float x, float z) =>
        HasSecondary ? x * SecondaryNormalX + z * SecondaryNormalZ - SecondaryOffset : float.PositiveInfinity;
    /// <summary>Distance to the warm sector's edge: the smaller of the two boundary distances.</summary>
    public float SignedDistance(float x, float z) => Math.Min(PrimaryDistance(x, z), SecondaryDistance(x, z));
    /// <summary>The boundary that decides <see cref="SignedDistance"/> here (the one you are behind, or the closer one).</summary>
    public string NearestKind(float x, float z) => PrimaryDistance(x, z) <= SecondaryDistance(x, z) ? Kind : SecondaryKind;

    /// <summary>A point on the main boundary at <paramref name="along"/>, moved into the moist side along the base normal.</summary>
    public (float X, float Z) Point(float along, float intoMoistSide)
    {
        float normal = Offset + Bend(along) + intoMoistSide;
        return (NormalX * normal - NormalZ * along, NormalZ * normal + NormalX * along);
    }
    public (float X, float Z) SecondaryPoint(float along) =>
        (SecondaryNormalX * SecondaryOffset - SecondaryNormalZ * along, SecondaryNormalZ * SecondaryOffset + SecondaryNormalX * along);

    /// <summary>Where the warm front meets the main boundary (bisection along the main boundary; the angle is kept at 45 degrees or more).</summary>
    public bool TryTriplePoint(out float x, out float z)
    {
        x = z = 0;
        if (!HasSecondary) return false;
        float low = -90000, high = 90000;
        var start = Point(low, 0); var end = Point(high, 0);
        float fLow = SecondaryDistance(start.X, start.Z), fHigh = SecondaryDistance(end.X, end.Z);
        if (MathF.Sign(fLow) == MathF.Sign(fHigh)) return false;
        for (int i = 0; i < 32; i++)
        {
            float middle = (low + high) * .5f; var p = Point(middle, 0);
            float f = SecondaryDistance(p.X, p.Z);
            if (MathF.Sign(f) == MathF.Sign(fLow)) { low = middle; fLow = f; } else high = middle;
        }
        (x, z) = Point((low + high) * .5f, 0);
        return true;
    }
    /// <summary>Unit direction from the triple point into the warm sector (between the two warm-side normals).</summary>
    public (float X, float Z) WarmSectorDirection()
    {
        float dx = NormalX + SecondaryNormalX, dz = NormalZ + SecondaryNormalZ, length = MathF.Max(1e-4f, MathF.Sqrt(dx * dx + dz * dz));
        return (dx / length, dz / length);
    }
}
