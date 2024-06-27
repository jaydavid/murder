﻿using Bang.Components;
using Bang.Contexts;
using Bang.Entities;
using Bang.Systems;
using ImGuiNET;
using Murder.Assets.Graphics;
using Murder.Components;
using Murder.Components.Graphics;
using Murder.Core;
using Murder.Core.Geometry;
using Murder.Core.Graphics;
using Murder.Core.Input;
using Murder.Editor.Components;
using Murder.Editor.Messages;
using Murder.Editor.Utilities;
using Murder.Helpers;
using Murder.Messages;
using Murder.Services;
using Murder.Utilities;
using System.Numerics;

namespace Murder.Editor.Systems;

[Filter(typeof(ITransformComponent))]
[Filter(filter: ContextAccessorFilter.AnyOf, typeof(SpriteComponent), typeof(AgentSpriteComponent))]
[Filter(ContextAccessorFilter.NoneOf, typeof(ThreeSliceComponent))]
[Filter(ContextAccessorFilter.NoneOf, typeof(InvisibleComponent))]
internal class SpriteRenderDebugSystem : IMurderRenderSystem, IGuiSystem
{
    private readonly static int _hash = typeof(DebugColliderRenderSystem).GetHashCode();
    private int _draggingY = -1;
    private int _hoverY = -1;

    public void Draw(RenderContext render, Context context)
    {
        bool issueSlowdownWarning = false;

        EditorHook hook = context.World.GetUnique<EditorComponent>().EditorHook;

        float overrideCurrentTime = -1;
        _hoverY = -1;

        if (hook is TimelineEditorHook timeline)
        {
            overrideCurrentTime = timeline.Time;
        }

        bool previewMode = Game.Input.Down(InputHelpers.OSActionModifier);

        foreach (var e in context.Entities)
        {
            SpriteComponent? sprite = e.TryGetSprite();
            AgentSpriteComponent? agentSprite = e.TryGetAgentSprite();
            IMurderTransformComponent transform = e.GetGlobalTransform();

            string animationId;
            SpriteAsset? asset;
            float start;
            ImageFlip flip = ImageFlip.None;

            float ySortOffsetRaw;

            Vector2 boundsOffset = Vector2.Zero;
            if (sprite.HasValue)
            {
                animationId = sprite.Value.CurrentAnimation;
                asset = Game.Data.TryGetAsset<SpriteAsset>(sprite.Value.AnimationGuid);
                start = 0;
                boundsOffset = sprite.Value.Offset;

                ySortOffsetRaw = sprite.Value.YSortOffset;
            }
            else
            {
                (animationId, asset, start, flip) = GetAgentSpriteSettings(e);

                ySortOffsetRaw = agentSprite is not null ? agentSprite.Value.YSortOffset : 0;
            }

            if (asset is null)
            {
                continue;
            }

            ySortOffsetRaw += transform.Y;

            AnimationOverloadComponent? overload = null;
            if (e.TryGetAnimationOverload() is AnimationOverloadComponent o)
            {
                overload = o;
                animationId = o.CurrentAnimation;

                start = o.Start;
                if (o.CustomSprite is SpriteAsset customSprite)
                {
                    asset = customSprite;
                }

                ySortOffsetRaw += o.SortOffset;
            }

            Vector2 renderPosition;
            if (e.TryGetParallax() is ParallaxComponent parallax)
            {
                renderPosition = (transform.Vector2 + render.Camera.Position * (1 - parallax.Factor)).Point();
            }
            else
            {
                renderPosition = transform.Point;
            }

            // Handle alpha
            float alpha;
            if (e.TryGetAlpha() is AlphaComponent alphaComponent)
            {
                alpha = alphaComponent.Alpha;
            }
            else
            {
                alpha = 1f;
            }

            // This is as early as we can to check for out of bounds
            if (!render.Camera.Bounds.Touches(new Rectangle(renderPosition - asset.Size * boundsOffset - asset.Origin, asset.Size)))
                continue;

            Vector2 offset = sprite.HasValue ? sprite.Value.Offset : Vector2.Zero;
            Batch2D batch = sprite.HasValue ? render.GetBatch(sprite.Value.TargetSpriteBatch) :
                render.GameplayBatch;

            int ySortOffset = sprite.HasValue ? sprite.Value.YSortOffset : agentSprite!.Value.YSortOffset;


            bool showHandles = !previewMode &&
                (hook.EditorMode == EditorHook.EditorModes.EditMode && hook.IsEntitySelectedOrParent(e));

            if (showHandles && !hook.UsingGui && hook.CursorWorldPosition is Point cursorPosition)
            {
                Color color;
                if (_draggingY==e.EntityId)
                {
                    color = Architect.Profile.Theme.HighAccent;
                    
                    int newYSortOffset = (int)((cursorPosition.Y - transform.Vector2.Y) );
                    if (sprite != null)
                    {
                        e.SetSprite(sprite.Value with { YSortOffset = newYSortOffset });
                    }
                    else if (agentSprite != null)
                    {
                        e.SetAgentSprite(agentSprite.Value with { YSortOffset = newYSortOffset });
                    }

                    if (!Game.Input.Down(MurderInputButtons.LeftClick))
                    {
                        hook.CursorIsBusy.Remove(typeof(SpriteRenderDebugSystem));
                        _draggingY = -1;
                        if (sprite != null)
                        {
                            e.SendMessage(new AssetUpdatedMessage(typeof(SpriteComponent)));
                        }
                        else if (agentSprite != null)
                        {
                            e.SendMessage(new AssetUpdatedMessage(typeof(AgentSpriteComponent)));
                        }
                    }
                }
                else
                {
                    if (hook.HideEditIds.Contains(e.EntityId))
                    {
                        color = Color.Red * 0.1f;
                    }
                    else if (
                        cursorPosition.Y > transform.Y + (ySortOffset - 3) &&
                        cursorPosition.Y < transform.Y + (ySortOffset + 3) )
                    {
                        color = Color.White;
                        if (Game.Input.Pressed(MurderInputButtons.LeftClick) && !hook.CursorIsBusy.Any())
                        {
                            hook.CursorIsBusy.Add(typeof(SpriteRenderDebugSystem));
                            _draggingY = e.EntityId;
                            _hoverY = e.EntityId;
                        }

                        if (_draggingY < 0)
                        {
                            _hoverY = e.EntityId;
                        }
                    }
                    else
                    {
                        color = Color.BrightGray;
                    }
                }

                RenderServices.DrawHorizontalLine(
                render.DebugBatch,
                (int)render.Camera.Bounds.Left,
                (int)(transform.Y + ySortOffset),
                (int)render.Camera.Bounds.Width,
                color,
                0.2f);

            }

            float rotation = transform.Angle;

            if (e.TryGetRotation() is RotationComponent RotationComponent)
            {
                rotation += RotationComponent.Rotation;
            }

            if (sprite.HasValue && e.TryGetFacing() is FacingComponent facing)
            {
                if (sprite.Value.RotateWithFacing)
                    rotation += facing.Angle;

                if (sprite.Value.FlipWithFacing)
                {
                    flip = facing.Direction.GetFlippedHorizontal();
                }

                if (e.TryGetSpriteFacing() is SpriteFacingComponent spriteFacing)
                {
                    (animationId, var horizontalFlip)= spriteFacing.GetSuffixFromAngle(facing.Angle);
                    flip = horizontalFlip ? ImageFlip.Horizontal : ImageFlip.None;
                }
            }

            float ySort = RenderServices.YSort(ySortOffsetRaw);

            Color baseColor = e.TryGetTint()?.TintColor ?? Color.White;
            if (e.HasComponent<IsPlacingComponent>())
            {
                baseColor *= .5f;
            }
            else
            {
                baseColor *= alpha;
            }

            Vector2 scale = e.TryGetScale()?.Scale ?? Vector2.One;

            // Cute tween effect when placing components, if any.
            float placedTime = e.TryGetComponent<PlacedInWorldComponent>()?.PlacedTime ?? 0;
            if (placedTime != 0)
            {
                float modifier = Calculator.ClampTime(Game.NowUnscaled - placedTime, .75f);
                if (modifier == 1)
                {
                    e.RemoveComponent<PlacedInWorldComponent>();
                }

                scale -= Ease.ElasticIn(.9f - modifier * .9f) * scale;
            }

            Rectangle clip = Rectangle.Empty;
            if (e.TryGetSpriteClippingRect() is SpriteClippingRectComponent spriteClippingRect)
            {
                clip = spriteClippingRect.GetClippingRect(asset.Size);
                renderPosition += new Vector2(clip.Left, clip.Top);
            }

            if (e.TryGetSpriteOffset() is SpriteOffsetComponent spriteOffset)
            {
                renderPosition += spriteOffset.Offset;
            }

            bool isSelected = !previewMode && e.HasComponent<IsSelectedComponent>();

            if (e.TryGetComponent<EditorTween>() is EditorTween editorTween)
            {
                float delta = Calculator.ClampTime(Game.NowUnscaled - editorTween.StartTime, editorTween.Duration);
                if (editorTween.Type == EditorTweenType.Lift)
                {
                    scale *= 1 + 0.05f * Ease.BounceIn(1 - delta);
                    rotation = rotation + 0.05f * Ease.JumpArc(delta);
                    isSelected = false;
                }
                else if (editorTween.Type == EditorTweenType.Place)
                {
                    scale *= 1 + 0.1f * Ease.BounceIn(1 - delta);
                }
                else if (editorTween.Type == EditorTweenType.Move && delta < 1)
                {
                    isSelected = false;
                }
            }


            AnimationInfo animationInfo = new AnimationInfo(animationId, start) with { OverrideCurrentTime = overrideCurrentTime };
            FrameInfo frameInfo = RenderServices.DrawSprite(
                batch,
                asset,
                renderPosition,
                new DrawInfo()
                {
                    Clip = clip,
                    Origin = offset,
                    ImageFlip = flip,
                    Rotation = rotation,
                    Sort = ySort,
                    Scale = scale,
                    Color = baseColor,
                    Outline = isSelected ? Color.White.FadeAlpha(0.65f) : null,
                },
                animationInfo);

            issueSlowdownWarning = RenderServices.TriggerEventsIfNeeded(e, asset.Guid, animationInfo, frameInfo);

            e.SetRenderedSpriteCache(new RenderedSpriteCacheComponent() with
            {
                RenderedSprite = asset.Guid,
                CurrentAnimation = frameInfo.Animation,
                RenderPosition = renderPosition,
                Offset = sprite?.Offset ?? Vector2.Zero,
                ImageFlip = flip,
                Rotation = rotation,
                Scale = scale,
                Color = baseColor,
                Outline = sprite?.HighlightStyle ?? OutlineStyle.None,
                AnimInfo = animationInfo,
                Sorting = ySort,
                LastFrameIndex = frameInfo.InternalFrame
            });

            if (frameInfo.Complete && overload != null)
            {
                if (overload.Value.AnimationCount > 1)
                {
                    if (overload.Value.Current < overload.Value.AnimationCount - 1)
                    {
                        e.SetAnimationOverload(overload.Value.PlayNext());
                    }
                    else
                    {
                        e.SendMessage<AnimationCompleteMessage>();
                    }
                }
                else if (!overload.Value.Loop)
                {
                    e.RemoveAnimationOverload();
                    e.SendMessage<AnimationCompleteMessage>();
                }
                else
                {
                    e.SendMessage<AnimationCompleteMessage>();
                }
            }

            if (hook.ShowReflection && e.TryGetReflection() is ReflectionComponent reflection && !reflection.BlockReflection)
            {
                RenderServices.DrawSprite(
                    render.FloorBatch,
                    asset.Guid,
                    renderPosition + reflection.Offset,
                    new DrawInfo()
                    {
                        Origin = offset,
                        ImageFlip = flip,
                        Rotation = rotation,
                        Sort = 0,
                        Color = baseColor * reflection.Alpha,
                        Scale = scale * new Vector2(1, -1),
                    },
                    new AnimationInfo(animationId, start) with { OverrideCurrentTime = overrideCurrentTime });
            }
        }

        if (issueSlowdownWarning)
        {
            // Do nothing! But we could issue an warning somewhere.
        }
    }

    private (string animationId, SpriteAsset? asset, float start, ImageFlip flip) GetAgentSpriteSettings(Entity e)
    {
        AgentSpriteComponent sprite = e.GetAgentSprite();
        FacingComponent facing = e.GetFacing();

        float start = NoiseHelper.Simple01(e.EntityId * 10) * 5f;
        var prefix = sprite.IdlePrefix;

        var angle = facing.Angle; // Gives us an angle from 0 to 1, with 0 being right and 0.5 being left
        (string suffix, bool flip) = DirectionHelper.GetSuffixFromAngle(e, sprite, angle);

        SpriteAsset? SpriteAsset = Game.Data.TryGetAsset<SpriteAsset>(sprite.AnimationGuid);

        return (prefix + suffix, SpriteAsset, start, flip ? ImageFlip.Horizontal : ImageFlip.None);
    }

    public void DrawGui(RenderContext render, Context context)
    {
        if (_draggingY > 0 && context.World.TryGetEntity(_draggingY) is Entity entity)
        {
            EditorHook hook = context.World.GetUnique<EditorComponent>().EditorHook;

            if (ImGui.BeginTooltip())
            {
                ImGui.Text($"Dragging ({entity.EntityId}) {hook.GetNameForEntityId?.Invoke(entity.EntityId)}");
                ImGui.Text($"YSort: {entity.TryGetSprite()?.YSortOffset}");

                ImGui.EndTooltip();
            }
        }
        else
        {
            if (_hoverY > 0 && context.World.TryGetEntity(_hoverY) is Entity hovered)
            {
                EditorHook hook = context.World.GetUnique<EditorComponent>().EditorHook;

                if (ImGui.BeginTooltip())
                {
                    ImGui.Text($"YSort for ({hovered.EntityId}) {hook.GetNameForEntityId?.Invoke(hovered.EntityId)}");
                    ImGui.EndTooltip();
                }
            }
        }
    }
}