using HarmonyLib;
using Sprocket.Vehicles;
using Sprocket.Vehicles.Turrets;

namespace SprocketQoL;

/// Native ring initialization subscribes to its motor's transform event, but releasing
/// the ring does not unsubscribe. Remove that listener before destruction so moving a
/// surviving motor cannot validate against a destroyed ring's Unity transform.
[HarmonyPatch]
public static class TurretMotorLifecycle
{
    // The object register's deletion path destroys the GameObject without calling
    // VehicleObject.Release. Clean up before that path, while the ring is still live.
    [HarmonyPrefix, HarmonyPatch(typeof(VehicleObjectRegister), nameof(VehicleObjectRegister.ExecuteDestruction), new[] { typeof(VehicleObject) })]
    static void BeforeDestroy(VehicleObject obj) => Ui.Guard("Turret motor lifecycle", () =>
    {
        if (obj == null || obj.behaviours is not { } components) return;
        foreach (var component in components)
            if (component?.TryCast<TurretRing>() is { } ring) DetachCurrent(ring);
    });

    [HarmonyPrefix, HarmonyPatch(typeof(VehicleComponent), nameof(VehicleComponent.ReleaseInternal))]
    static void BeforeRelease(VehicleComponent __instance)
    {
        if (__instance.TryCast<TurretRing>() is not { } ring) return;
        Ui.Guard("Turret motor lifecycle", () => DetachCurrent(ring));
    }

    // Native initialization always adds another listener, including when loading an
    // existing ring's state. Make it idempotent without touching other listeners.
    [HarmonyPrefix, HarmonyPatch(typeof(TurretRing), nameof(TurretRing.InitiateMotor))]
    static bool BeforeInitiate(TurretRing __instance, TraverseMotor motor)
    {
        if (__instance == null || motor == null) return false;
        Ui.Guard("Turret motor lifecycle", () =>
        {
            DetachCurrent(__instance);
            Detach(__instance, motor.VehicleTransform);
        });
        return true;
    }

    // LoadDataInternal replaces ring.motor before InitiateMotor sees it. Keep the
    // former event source for this call only, then detach if loading changed motors.
    [HarmonyPrefix, HarmonyPatch(typeof(TurretRing), nameof(TurretRing.LoadDataInternal))]
    static void BeforeLoad(TurretRing __instance, out VehicleTransform? __state)
    {
        VehicleTransform? previous = null;
        Ui.Guard("Turret motor lifecycle", () =>
        {
            if (__instance != null && __instance.motor != null) previous = __instance.motor.VehicleTransform;
        });
        __state = previous;
    }

    [HarmonyPostfix, HarmonyPatch(typeof(TurretRing), nameof(TurretRing.LoadDataInternal))]
    static void AfterLoad(TurretRing __instance, VehicleTransform? __state) => Ui.Guard("Turret motor lifecycle", () =>
    {
        if (__instance is null || __state == null) return;
        var current = __instance.motor != null ? __instance.motor.VehicleTransform : null;
        if (current == null || current.Pointer != __state.Pointer) Detach(__instance, __state);
    });

    // A listener created before this patch, or already in the invocation snapshot while
    // its ring is released, must also stop before native code reads Component.transform.
    [HarmonyPrefix, HarmonyPatch(typeof(TurretRing), nameof(TurretRing.OnMotorTransformChanged))]
    static bool BeforeMotorMoved(TurretRing __instance, VehicleTransform arg1)
    {
        if (__instance != null && __instance.motor != null) return true;
        if (__instance is not null)
            Ui.Guard("Turret motor lifecycle", () => Detach(__instance, arg1));
        return false;
    }

    [HarmonyPrefix, HarmonyPatch(typeof(TurretRing), nameof(TurretRing.ValidateMotorPosition))]
    static bool BeforeValidate(TurretRing __instance, TraverseMotor motor) => __instance != null && motor != null;

    static void DetachCurrent(TurretRing ring)
    {
        if (ring != null && ring.motor is { } motor && motor != null) Detach(ring, motor.VehicleTransform);
    }

    static void Detach(TurretRing ring, VehicleTransform transform)
    {
        if (transform == null || transform.TransformChanged is not { } listeners) return;
        // Remove only this ring's native motor callback. Other rings, parts and any
        // third-party listeners on the same transform retain their original order.
        foreach (var listener in listeners.GetInvocationList())
            if (listener.Target?.Pointer == ring.Pointer && listener.Method?.Name == nameof(TurretRing.OnMotorTransformChanged))
                transform.remove_TransformChanged(listener.Cast<VehicleTransformChangedHandler>());
    }
}
