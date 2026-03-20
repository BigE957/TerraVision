using Daybreak.Common.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ReLogic.Graphics;
using System.Collections.Generic;
using Terraria;
using Terraria.GameContent;

namespace TerraVision.Core.VideoPlayer.Danmaku;

public class DanmakuRenderer
{
    // How long a scrolling comment takes to cross the screen
    private const float ScrollDuration = 8f;

    // How long anchored comments stay visible
    private const float AnchorDuration = 5f;

    // Fraction of video height available for danmaku
    private const float ScreenCoverage = 0.85f;

    private const float TextScale = 0.75f;

    private float LineHeight => FontAssets.MouseText.Value.MeasureString("A").Y * TextScale + 2f;

    // Comments with precomputed lane assignments, set once on load
    private List<DanmakuComment> _comments = [];

    /// <summary>
    /// Assigns lanes to all comments up front so Draw is purely stateless.
    /// Uses a reference width to determine scroll overlap; scales fine at runtime.
    /// </summary>
    public void LoadComments(List<DanmakuComment> comments, float referenceWidth = 1280f)
    {
        _comments = comments;

        float lineHeight = LineHeight;
        float usableHeight = referenceWidth * 0.5625f * ScreenCoverage; // assume 16:9
        int maxLanes = (int)(usableHeight / lineHeight);

        // Per-lane, track the video time at which the lane becomes free again
        var scrollLaneFreeAt = new float[maxLanes];
        var topLaneFreeAt = new float[maxLanes];
        var bottomLaneFreeAt = new float[maxLanes];

        var font = FontAssets.MouseText.Value;

        foreach (var comment in _comments)
        {
            float textWidth = font.MeasureString(comment.Text).X * TextScale;

            switch (comment.Type)
            {
                case 1: // scrolling
                    {
                        // A lane is free when the previous comment's tail has cleared
                        // the right edge, giving the new comment room to enter.
                        // Since all comments scroll at the same speed, the tail of the
                        // previous comment clears the right edge when enough time has
                        // passed for it to travel (referenceWidth + prevTextWidth).
                        // That takes: (prevTextWidth / (referenceWidth + prevTextWidth)) * ScrollDuration
                        // after its own appear time. We simplify: lane is free once
                        // the tail has exited, i.e. prevAppear + ScrollDuration * (prevTextWidth / totalTravel)
                        // For safety we just require the lane to be free at comment.Time.
                        int lane = FindFreeLane(scrollLaneFreeAt, comment.Time, maxLanes);
                        comment.Lane = lane >= 0 ? lane : -1;

                        if (lane >= 0)
                        {
                            // Lane stays occupied until the new comment's tail clears the right edge
                            float totalTravel = referenceWidth + textWidth;
                            float timeToExit = ScrollDuration * (textWidth / totalTravel);
                            scrollLaneFreeAt[lane] = comment.Time + timeToExit;
                        }
                        break;
                    }
                case 5: // top anchored
                    {
                        int lane = FindFreeLane(topLaneFreeAt, comment.Time, maxLanes);
                        comment.Lane = lane >= 0 ? lane : -1;
                        if (lane >= 0)
                            topLaneFreeAt[lane] = comment.Time + AnchorDuration;
                        break;
                    }
                case 4: // bottom anchored
                    {
                        int lane = FindFreeLane(bottomLaneFreeAt, comment.Time, maxLanes);
                        comment.Lane = lane >= 0 ? lane : -1;
                        if (lane >= 0)
                            bottomLaneFreeAt[lane] = comment.Time + AnchorDuration;
                        break;
                    }
            }
        }
    }

    private static int FindFreeLane(float[] laneFreeAt, float commentTime, int maxLanes)
    {
        for (int i = 0; i < maxLanes; i++)
        {
            if (laneFreeAt[i] <= commentTime)
                return i;
        }
        return -1;
    }

    public void Clear() => _comments.Clear();

    public void Draw(SpriteBatch spriteBatch, Rectangle videoRect, float currentTime)
    {
        if (_comments.Count == 0)
            return;

        var font = FontAssets.MouseText.Value;

        float playerScale = videoRect.Width / 640f;
        float scale = TextScale * playerScale;

        float lineHeight = LineHeight * playerScale;
        var graphicsDevice = Main.graphics.GraphicsDevice;

        Rectangle previousScissor = graphicsDevice.ScissorRectangle;

        Rectangle clipRect = videoRect;

        spriteBatch.End(out var scope);
        var oldScope = scope;

        if (scope.TransformMatrix != Matrix.Identity)
        {
            Vector2[] corners = [new(clipRect.Left, clipRect.Top), new(clipRect.Right, clipRect.Top), new(clipRect.Left, clipRect.Bottom), new(clipRect.Right, clipRect.Bottom)];
            for (int i = 0; i < 4; i++)
                corners[i] = Vector2.Transform(corners[i], Main.UIScaleMatrix);

            Vector2 min = Vector2.Min(Vector2.Min(corners[0], corners[1]), Vector2.Min(corners[2], corners[3]));
            Vector2 max = Vector2.Max(Vector2.Max(corners[0], corners[1]), Vector2.Max(corners[2], corners[3]));

            clipRect = new Rectangle((int)min.X, (int)min.Y, (int)(max.X - min.X), (int)(max.Y - min.Y));
        }  

        scope.RasterizerState = new RasterizerState { ScissorTestEnable = true };
        graphicsDevice.ScissorRectangle = clipRect;
        spriteBatch.Begin(scope);

        foreach (var comment in _comments)
        {
            if (comment.Lane < 0)
                continue;

            float age = currentTime - comment.Time;
            if (age < 0)
                continue;

            float duration = comment.Type == 1 ? ScrollDuration : AnchorDuration;
            if (age > duration)
                continue;

            float textWidth = font.MeasureString(comment.Text).X * scale;
            float x, y;

            switch (comment.Type)
            {
                case 1: // scrolling right to left
                    {
                        float totalTravel = videoRect.Width + textWidth;
                        float progress = age / ScrollDuration;
                        x = videoRect.Width - progress * totalTravel;
                        y = comment.Lane * lineHeight;
                        break;
                    }
                case 5: // top anchored
                    x = (videoRect.Width - textWidth) / 2f;
                    y = comment.Lane * lineHeight;
                    break;
                case 4: // bottom anchored, lanes stack upward
                    x = (videoRect.Width - textWidth) / 2f;
                    y = videoRect.Height - (comment.Lane + 1) * lineHeight;
                    break;
                default:
                    continue;
            }

            // Fade out over the last 20% of the comment's lifetime
            float fadeStart = duration * 0.8f;
            float alpha = age > fadeStart ? 1f - ((age - fadeStart) / (duration - fadeStart)) : 1f;

            var color = new Color((comment.Color >> 16) & 0xFF, (comment.Color >> 8) & 0xFF, comment.Color & 0xFF) * alpha;

            Utils.DrawBorderString(spriteBatch, comment.Text, videoRect.TopLeft() + new Vector2(x, y), color, playerScale);
        }

        spriteBatch.End();
        graphicsDevice.ScissorRectangle = previousScissor;
        spriteBatch.Begin(oldScope);
    }
}