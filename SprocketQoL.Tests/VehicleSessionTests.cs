using SprocketQoL;

/// Review CORE-01: an edit belongs to the vehicle it was asked for on.
static class VehicleSessionTests
{
    internal static void Run()
    {
        int checks = 0;
        void Check(bool ok, string reason) { checks++; if (!ok) throw new Exception("Vehicle session: " + reason); }
        IntPtr a = 100, a2 = 200, b = 300;

        var s = new VehicleSession();
        s.Saw(a);
        int asked = s.Now;
        s.Saw(a);
        Check(s.Now == asked, "the same vehicle frame after frame is one session");
        s.Saw(b);
        Check(s.Now != asked, "an edit queued on A is stale once B is opened");

        s = new VehicleSession();
        s.Saw(a); asked = s.Now;
        s.Loading();
        s.Left(); s.Saw(IntPtr.Zero); // the editor may stop while QoL's own reload runs
        s.Saw(a2); s.Loaded();
        Check(s.Now == asked, "QoL's own reload keeps the session: its Restore and follow-up stay");

        s = new VehicleSession();
        s.Saw(a); asked = s.Now;
        s.Loading();
        s.Saw(a2); s.Saw(b);
        s.Loaded();
        Check(s.Now != asked, "a vehicle opened after QoL's reload makes that reload's result stale");

        s = new VehicleSession();
        s.Saw(a); asked = s.Now;
        s.Saw(b); s.Saw(a);
        Check(s.Now != asked, "the pointer of A coming back after B is still a new session");

        s = new VehicleSession();
        s.Saw(a); asked = s.Now;
        s.Left(); s.Saw(a);
        Check(s.Now != asked, "leaving the editor and coming back ends the session, even on the same vehicle");

        s = new VehicleSession();
        s.Saw(a); asked = s.Now;
        s.Loading(); s.Loaded(); // a reload that failed: the vehicle never changed
        s.Saw(b);
        Check(s.Now != asked, "after a failed reload the next vehicle is someone else's again");
        Console.WriteLine($"VEHICLE_SESSION_TESTS_OK: {checks} checks");
    }
}
