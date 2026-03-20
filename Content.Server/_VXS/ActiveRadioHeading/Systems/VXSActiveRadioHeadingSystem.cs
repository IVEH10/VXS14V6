using System.Numerics;
using Content.Server._VXS.ActiveRadioHeading.Components;
using Content.Server.Shuttles.Components;
using Content.Shared._VXS.Manpads.Components;
using Content.Shared.Interaction;
using Content.Shared.Projectiles;
using Robust.Server.GameObjects;

namespace Content.Server._VXS.ActiveRadioHeading.Systems;

public sealed class VXSActiveRadioHeadingSystem : EntitySystem
{
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly RotateToFaceSystem _rotate = default!;
    [Dependency] private readonly PhysicsSystem _physics = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;

    public override void Update(float frameTime)
    {
        var query = EntityQueryEnumerator<VXSActiveRadioHeadingComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var comp, out var xform))
        {
            if (comp.Speed < comp.InitialSpeed)
                comp.Speed = comp.InitialSpeed;

            if (comp.Speed < comp.TopSpeed)
                comp.Speed += comp.Acceleration * frameTime;
            else
                comp.Speed = comp.TopSpeed;

            _physics.SetLinearVelocity(uid, _transform.GetWorldRotation(xform).ToWorldVec() * comp.Speed);

            if (!TerminatingOrDeleted(comp.TargetEntity)) // also checks for nullity
            {
                if ((comp.GuidanceAlgorithm & GuidanceType.PredictiveGuidance) != 0)
                    PredictiveGuidance(uid, comp, xform, frameTime);
                else if ((comp.GuidanceAlgorithm & GuidanceType.PurePursuit) != 0)
                    PurePursuit(uid, comp, xform, frameTime);
                else
                    PredictiveGuidance(uid, comp, xform, frameTime);
            }
            else
            {
                GetNewTarget(uid, comp, xform);
            }
        }
    }

    public void SetNewTarget(Entity<VXSActiveRadioHeadingComponent?> ent, EntityUid newTarget)
    {
        if (!Resolve(ent, ref ent.Comp))
            return;
        Log.Debug($"NEW TRGT {newTarget} {MetaData(newTarget).EntityName}");
        ent.Comp.TargetEntity = newTarget;
        RaiseTargetLockedEvent(ent, newTarget);
    }

    private void RaiseTargetLockedEvent(EntityUid sender, EntityUid target)
    {
        RaiseLocalEvent(target, new VXSTargetLockedEvent(sender)); // Raise ev on targetGrid

        var entities = new HashSet<Entity<VXSTargetLockReceiverComponent>>();
        _lookup.GetChildEntities(target, entities);

        foreach (var entity in entities)
        {
            RaiseLocalEvent(entity, new VXSTargetLockedEvent(sender)); // raise ev for everyone on grid
        }
    }

    private EntityUid? FindClosestTargetInEnumerator<T>(
        VXSActiveRadioHeadingComponent missileComp,
        TransformComponent missileXform,
        EntityQueryEnumerator<T, TransformComponent> query,
        EntityUid? shooterGridUid,
        VXSManpadsIffType? shooterIffType = null)
        where T : IComponent
    {
        var closestDistance = float.MaxValue;
        EntityUid? closestTargetUid = null;

        var radioHeadingPos = _transform.ToMapCoordinates(missileXform.Coordinates).Position;
        var worldRotation = _transform.GetWorldRotation(missileXform);
        var halfFovRad = missileComp.FOV * Math.PI / 180f;
        var seekRangeSq = missileComp.SeekRange * missileComp.SeekRange;

        while (query.MoveNext(out var targetUid, out _, out var targetXform))
        {
            var targetPos = _transform.ToMapCoordinates(targetXform.Coordinates).Position;

            var distanceSq = Vector2.DistanceSquared(radioHeadingPos, targetPos);
            if (distanceSq > seekRangeSq)
                continue;

            var angle = (targetPos - radioHeadingPos).ToWorldAngle();
            var angleDifference = Angle.ShortestDistance(angle, worldRotation);

            if (Math.Abs(angleDifference) > halfFovRad)
                continue;

            if (shooterGridUid.HasValue)
            {
                if (targetXform.GridUid.HasValue && shooterGridUid.Value == targetXform.GridUid.Value)
                    continue;
            }

            if (shooterIffType.HasValue && targetXform.GridUid.HasValue && GetGridIffType(targetXform.GridUid.Value) == shooterIffType.Value)
                continue;

            var distance = MathF.Sqrt(distanceSq);
            if (!(distance < closestDistance))
                continue;
            closestDistance = distance;
            closestTargetUid = targetUid;
        }

        return closestTargetUid;
    }

    private void GetNewTarget(EntityUid uid, VXSActiveRadioHeadingComponent component, TransformComponent transform)
    {
        EntityUid? shooterGridUid = null;
        VXSManpadsIffType? shooterIffType = null;
        if (TryComp<ProjectileComponent>(uid, out var projectile) &&
            TryComp<TransformComponent>(projectile.Shooter, out var shooterTransform))
        {
            shooterGridUid = shooterTransform.GridUid;
            if (shooterGridUid.HasValue)
                shooterIffType = GetGridIffType(shooterGridUid.Value);
        }

        var retargetQuery = EntityQueryEnumerator<VXSRetargetComponent, TransformComponent>();
        var retargetTargetEntity =
            FindClosestTargetInEnumerator(component, transform, retargetQuery, shooterGridUid, shooterIffType);

        if (retargetTargetEntity is not null)
        {
            SetNewTarget((uid, component), retargetTargetEntity.Value);
            return;
        }

        var consoleQuery = EntityQueryEnumerator<ShuttleConsoleComponent, TransformComponent>();
        var consoleTargetEntity =
            FindClosestTargetInEnumerator(component, transform, consoleQuery, shooterGridUid, shooterIffType);

        if (!consoleTargetEntity.HasValue)
            return;
        if (TryComp(consoleTargetEntity.Value, out TransformComponent? consoleTargetXform) &&
            consoleTargetXform.GridUid.HasValue)
        {
            SetNewTarget((uid, component), consoleTargetXform.GridUid.Value);
        }
    }

    private VXSManpadsIffType? GetGridIffType(EntityUid gridUid)
    {
        var children = new HashSet<Entity<VXSIffTransponderComponent>>();
        _lookup.GetChildEntities(gridUid, children);
        foreach (var child in children)
            return child.Comp.IffType;
        return null;
    }

    private void PredictiveGuidance(EntityUid uid,
        VXSActiveRadioHeadingComponent comp,
        TransformComponent xform,
        float frameTime)
    {
        var oldDistance = comp.OldDistance;
        var oldPosition = comp.OldPosition;
        var entXform = Transform(comp.TargetEntity!.Value);

        var distance = Vector2.Distance(
            _transform.ToMapCoordinates(xform.Coordinates).Position,
            _transform.ToMapCoordinates(entXform.Coordinates).Position);

        var targetVelocity = _transform.ToMapCoordinates(entXform.Coordinates).Position - oldPosition;
        var timeToImpact = distance / (oldDistance - distance);
        if (timeToImpact < 0.1)
            timeToImpact = 0.1f;

        var predictedPosition =
            _transform.ToMapCoordinates(entXform.Coordinates).Position + targetVelocity * timeToImpact;
        var targetAngle = (predictedPosition - _transform.ToMapCoordinates(xform.Coordinates).Position).ToWorldAngle();

        _rotate.TryRotateTo(uid,
            targetAngle,
            frameTime,
            comp.WeaponArc,
            comp.RotationSpeed?.Theta ?? double.MaxValue,
            xform);

        comp.OldPosition = _transform.ToMapCoordinates(entXform.Coordinates).Position;
        comp.OldDistance = distance;
    }

    private void PurePursuit(EntityUid uid,
        VXSActiveRadioHeadingComponent comp,
        TransformComponent xform,
        float frameTime)
    {
        var entXform = Transform(comp.TargetEntity!.Value);
        var angle = (_transform.ToMapCoordinates(entXform.Coordinates).Position -
                     _transform.ToMapCoordinates(xform.Coordinates).Position).ToWorldAngle();

        _rotate.TryRotateTo(uid, angle, frameTime, comp.WeaponArc, comp.RotationSpeed?.Theta ?? double.MaxValue, xform);
    }
}
