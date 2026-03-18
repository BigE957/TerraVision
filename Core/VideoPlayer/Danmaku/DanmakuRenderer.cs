using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ReLogic.Graphics;
using System;
using System.Collections.Generic;
using Terraria;
using Terraria.GameContent;

namespace TerraVision.Core.VideoPlayer.Danmaku;

/// <summary>
/// A comment currently being displayed on screen.
/// </summary>
public class ActiveDanmakuComment
{
    public DanmakuComment Source { get; init; }

    /// <summary>Current screen-space position relative to the video rect's top-left.</summary>
    public float X { get; set; }
    public float Y { get; set; }

    /// <summary>How long this comment has been visible, in seconds.</summary>
    public float Age { get; set; }

    /// <summary>Pre-measured text width in pixels at scale 1.0.</summary>
    public float TextWidth { get; init; }

    /// <summary>Which lane (row) this comment occupies.</summary>
    public int Lane { get; init; }

    public bool IsExpired(float scrollDuration, float anchorDuration)
    {
        float duration = Source.Type == 1 ? scrollDuration : anchorDuration;
        return Age >= duration;
    }
}

/// <summary>
/// Manages active danmaku comments — lane assignment, movement, and drawing.
/// </summary>
public class DanmakuRenderer
{
    private readonly List<ActiveDanmakuComment> _active = new();

    // How long a scrolling comment takes to cross the screen
    private const float ScrollDuration = 8f;

    // How long anchored (top/bottom) comments stay visible
    private const float AnchorDuration = 5f;

    // Fraction of the video height used for danmaku (avoids covering the whole screen)
    private const float ScreenCoverage = 0.85f;

    // Scale of danmaku text relative to Terraria's mouse font
    private const float TextScale = 0.75f;

    // Approximate line height in pixels at TextScale
    private float LineHeight => FontAssets.MouseText.Value.MeasureString("A").Y * TextScale + 2f;

    /// <summary>
    /// Call once per frame. Advances comment positions and removes expired ones.
    /// </summary>
    public void Update(float deltaSeconds, Vector2 videoSize)
    {
        float scrollPixelsPerSecond = (videoSize.X * 1.5f) / ScrollDuration;

        for (int i = _active.Count - 1; i >= 0; i--)
        {
            var c = _active[i];
            c.Age += deltaSeconds;

            if (c.IsExpired(ScrollDuration, AnchorDuration))
            {
                _active.RemoveAt(i);
                continue;
            }

            // Scrolling comments move right to left
            if (c.Source.Type == 1)
                c.X -= scrollPixelsPerSecond * deltaSeconds;
        }
    }

    /// <summary>
    /// Activates a new comment, assigning it a lane and starting position.
    /// Call this from VideoPlayerCore when the comment's timestamp is reached.
    /// </summary>
    public void Activate(DanmakuComment comment, Vector2 videoSize)
    {
        float textWidth = FontAssets.MouseText.Value.MeasureString(comment.Text).X * TextScale;

        int lane = AssignLane(comment.Type, textWidth, videoSize);
        if (lane < 0)
            return; // all lanes full, drop this comment

        float startX, startY;
        float usableHeight = videoSize.Y * ScreenCoverage;
        float laneY = lane * LineHeight;

        switch (comment.Type)
        {
            case 1: // scrolling — starts off the right edge
                startX = videoSize.X;
                startY = laneY;
                break;
            case 4: // bottom anchored — lanes grow upward from the bottom
                startX = (videoSize.X - textWidth) / 2f;
                startY = videoSize.Y - (lane + 1) * LineHeight;
                break;
            case 5: // top anchored
            default:
                startX = (videoSize.X - textWidth) / 2f;
                startY = laneY;
                break;
        }

        _active.Add(new ActiveDanmakuComment
        {
            Source = comment,
            X = startX,
            Y = startY,
            Age = 0f,
            TextWidth = textWidth,
            Lane = lane
        });
    }

    /// <summary>
    /// Draws all active comments. Call this inside VideoPlayerCore.DrawCore
    /// after the video texture draw, with position = video rect top-left.
    /// </summary>
    public void Draw(SpriteBatch spriteBatch, Vector2 videoPosition, Vector2 videoSize)
    {
        if (_active.Count == 0)
            return;

        var font = FontAssets.MouseText.Value;
        var graphicsDevice = Main.graphics.GraphicsDevice;

        // Save current scissor state
        Rectangle previousScissor = graphicsDevice.ScissorRectangle;
        RasterizerState previousRasterizer = spriteBatch.GraphicsDevice.RasterizerState;

        // Define clipping rect matching the video area exactly
        Rectangle clipRect = new Rectangle(
            (int)videoPosition.X,
            (int)videoPosition.Y,
            (int)videoSize.X,
            (int)videoSize.Y
        );

        Vector2[] corners = {
            new Vector2(clipRect.Left, clipRect.Top),
            new Vector2(clipRect.Right, clipRect.Top),
            new Vector2(clipRect.Left, clipRect.Bottom),
            new Vector2(clipRect.Right, clipRect.Bottom)
        };
        for (int i = 0; i < 4; i++)
            corners[i] = Vector2.Transform(corners[i], Main.UIScaleMatrix);

        // Find min/max to create the new AABB
        Vector2 min = Vector2.Min(Vector2.Min(corners[0], corners[1]), Vector2.Min(corners[2], corners[3]));
        Vector2 max = Vector2.Max(Vector2.Max(corners[0], corners[1]), Vector2.Max(corners[2], corners[3]));

        clipRect = new Rectangle((int)min.X, (int)min.Y, (int)(max.X - min.X), (int)(max.Y - min.Y));

        // SpriteBatch must be ended and restarted with RasterizerState.CullNone + ScissorTestEnable
        spriteBatch.End();

        var scissorRasterizer = new RasterizerState { ScissorTestEnable = true };
        graphicsDevice.ScissorRectangle = clipRect;

        spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp, DepthStencilState.None, scissorRasterizer, null, Main.UIScaleMatrix);

        foreach (var c in _active)
        {
            Vector2 drawPos = videoPosition + new Vector2(c.X, c.Y);

            var color = new Color(
                (c.Source.Color >> 16) & 0xFF,
                (c.Source.Color >> 8) & 0xFF,
                 c.Source.Color & 0xFF
            );

            float duration = c.Source.Type == 1 ? ScrollDuration : AnchorDuration;
            float fadeStart = duration * 0.8f;
            float alpha = c.Age > fadeStart
                ? 1f - ((c.Age - fadeStart) / (duration - fadeStart))
                : 1f;

            color *= alpha;
            Utils.DrawBorderString(spriteBatch, c.Source.Text, drawPos, color, TextScale);
        }

        // Restore previous spritebatch state
        spriteBatch.End();

        graphicsDevice.ScissorRectangle = previousScissor;

        spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp, DepthStencilState.None, previousRasterizer, null, Main.UIScaleMatrix);
    }

    /// <summary>
    /// Clears all active comments. Call this on seek or video change.
    /// </summary>
    public void Clear() => _active.Clear();

    // -------------------------------------------------------------------------

    /// <summary>
    /// Finds the first lane that won't overlap with existing active comments.
    /// Returns -1 if no lane is available.
    /// </summary>
    private int AssignLane(int type, float newTextWidth, Vector2 videoSize)
    {
        float usableHeight = videoSize.Y * ScreenCoverage;
        int maxLanes = (int)(usableHeight / LineHeight);

        for (int lane = 0; lane < maxLanes; lane++)
        {
            if (!IsLaneOccupied(lane, type, newTextWidth, videoSize))
                return lane;
        }

        return -1;
    }

    /// <summary>
    /// Returns true if a comment in the given lane would overlap an existing one.
    /// </summary>
    private bool IsLaneOccupied(int lane, int type, float newTextWidth, Vector2 videoSize)
    {
        foreach (var c in _active)
        {
            if (c.Lane != lane || c.Source.Type != type)
                continue;

            if (type == 1)
            {
                // For scrolling comments, the lane is safe only if the existing comment
                // has fully entered the screen and won't be caught by the new one.
                // A new comment starts at videoSize.X and moves left. The existing
                // comment's right edge is at c.X + c.TextWidth.
                // The new comment catches up if it's faster — both move at the same speed,
                // so we just need the existing comment's tail to be fully on screen.
                bool existingTailOnScreen = (c.X + c.TextWidth) < videoSize.X;
                if (!existingTailOnScreen)
                    return true;
            }
            else
            {
                // Anchored comments: lane is occupied for their full duration
                return true;
            }
        }

        return false;
    }
}
