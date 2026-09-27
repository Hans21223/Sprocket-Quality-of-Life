namespace SprocketQoL;

/// Standing to top speed on flat ground at full throttle, stepped 50 times a second (no game code: tested offline).
internal static class Acceleration
{
    /// `torqueAt` the engine's torque (N·m) at a rev count, `resist` the rolling resistance and drag (N) at a speed (m/s).
    /// The gear that pushes hardest, counting the engine and sprockets it has to spin up; no drive while changing gear.
    /// Gathering speed, a driver only shifts up: going back down because the speed lost changing gear made the lower
    /// gear look better again had it change back and forth for ever. Down only when its gear can't go any faster. Stops
    /// at `top`, when nothing can push harder than the resistance, after 600 s or after 100 shifts.
    internal static (float Seconds, float Reached, int Shifts) Run(Func<float, float> torqueAt, Func<float, float> resist, float[] ratios,
        float finalDrive, float radius, float mass, float engineInertia, float sprocketInertia, float maxRpm, float shiftTime, float top)
    {
        float v = 0, t = 0;
        int gear = -1, shifts = 0;
        const float dt = 0.02f;
        float Push(int i, out bool fits)
        {
            float g = ratios[i] * finalDrive / radius;           // sprocket-to-ground and gearing, per metre
            float rpm = v * g * 30 / MathF.PI;
            fits = rpm <= maxRpm;
            return torqueAt(rpm) * g / (mass + engineInertia * g * g + sprocketInertia / (radius * radius));
        }
        while (v < top * 0.995f && t < 600)
        {
            bool stuck = gear >= 0 && (Push(gear, out bool inRange) - resist(v) / mass <= 1e-3f || !inRange);
            float best = 0;
            int pick = -1;
            for (int i = gear >= 0 && !stuck ? gear : 0; i < ratios.Length; i++)
            {
                float a = Push(i, out bool fits);
                if (fits && a > best) { best = a; pick = i; }
            }
            if (pick < 0 || shifts > 100) break;
            if (gear >= 0 && pick != gear)
            {
                // Changing gear: nothing drives, drag and rolling resistance still slow the vehicle.
                shifts++;
                for (float s = 0; s < shiftTime; s += dt) { v = Math.Max(0, v - resist(v) / mass * dt); t += dt; }
            }
            gear = pick;
            float net = Push(gear, out _) - resist(v) / mass;
            if (net <= 1e-3f) break;                                     // it can't go any faster
            v += net * dt;
            t += dt;
        }
        return (t, v, shifts);
    }
}
