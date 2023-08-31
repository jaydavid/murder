﻿using Bang.Contexts;
using Bang.Entities;
using Bang.Systems;
using Murder.Component;
using Murder.Components;
using Murder.Messages;

namespace Murder.Systems.Physics
{
    [Filter(typeof(VerticalPositionComponent))]
    public class VerticalPhysicsSystem : IFixedUpdateSystem
    {
        public void FixedUpdate(Context context)
        {
            foreach (var e in context.Entities)
            {
                float bounciness = 0.6f;
                if (e.TryGetBounceAmount() is BounceAmountComponent bounceOverride)
                    bounciness = bounceOverride.Bounciness;

                var verticalPosition = e.GetVerticalPosition().UpdatePosition(Game.FixedDeltaTime, bounciness);

                if (verticalPosition.Z == 0)
                {
                    e.SendMessage(new TouchedGroundMessage());
                    
                    if (verticalPosition.ZVelocity == 0)
                    {
                        e.RemoveVerticalPosition();
                        continue;
                    }
                }

                e.SetVerticalPosition(verticalPosition);
            }
        }
    }
}
