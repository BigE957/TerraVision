using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ReLogic.Graphics;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Terraria;
using Terraria.GameContent;

namespace TerraVision.Core.VideoPlayer.Captions;

public partial class CaptionRenderer
{
    private List<CaptionBlock> _captions = [];

    private const float TextScale = 0.85f;
    private const float PaddingX = 12f;
    private const float PaddingY = 8f;
    private const float BottomMargin = 24f;
    private const float BackgroundAlpha = 0.6f;
    private const float LineSpacing = 2f;

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    public void LoadCaptions(List<CaptionBlock> captions) => _captions = captions;

    public void Clear() => _captions.Clear();

    public void Draw(SpriteBatch spriteBatch, Rectangle videoRect, float currentTime, Texture2D pixel)
    {
        if (_captions.Count == 0)
            return;

        CaptionBlock active = FindActive(currentTime);
        if (active == null)
            return;

        var font = FontAssets.MouseText.Value;
        float lineH = font.MeasureString("A").Y * TextScale + LineSpacing;
        float maxWidth = videoRect.Width - PaddingX * 4f;

        // Bottom: words revealed progressively by timestamp
        string visibleText = BuildCurrentLine(active, currentTime);
        var bottomLines = string.IsNullOrWhiteSpace(visibleText) ? [] : WrapText(font, visibleText, maxWidth, TextScale);

        // Top: previous completed sentence
        var topLines = string.IsNullOrWhiteSpace(active.PreviousSentence) ? [] : WrapText(font, active.PreviousSentence, maxWidth, TextScale);

        int totalLines = topLines.Count + bottomLines.Count;
        if (totalLines == 0)
            return;

        // Use FullSentence for stable box width — prevents shifting as words appear
        string measureText = !string.IsNullOrWhiteSpace(active.FullSentence) ? active.FullSentence : string.Join(" ", active.Words.Select(w => w.Text));

        var bottomLinesFull = WrapText(font, measureText, maxWidth, TextScale);
        float stableWidth = bottomLinesFull.Count > 0 ? bottomLinesFull.Max(l => font.MeasureString(l).X * TextScale) : 0f;

        float actualMaxWidth = topLines.Count > 0 ? MathHelper.Max(stableWidth, topLines.Max(l => font.MeasureString(l).X * TextScale)) : stableWidth;

        float blockHeight = totalLines * lineH + PaddingY * 2f;
        float blockY = videoRect.Bottom - BottomMargin - blockHeight;

        // Background box
        Rectangle bgBox = new((int)(videoRect.X + (videoRect.Width - actualMaxWidth) / 2f - PaddingX), (int)(blockY - PaddingY), (int)(actualMaxWidth + PaddingX * 2f), (int)blockHeight);
        spriteBatch.Draw(pixel, bgBox, Color.Black * BackgroundAlpha);

        float currentY = blockY;

        // Previous sentence — slightly dimmed to visually separate from current line
        foreach (string line in topLines)
        {
            float lineX = videoRect.X +
                (videoRect.Width - font.MeasureString(line).X * TextScale) / 2f;
            Utils.DrawBorderString(spriteBatch, line,
                new Vector2(lineX, currentY), Color.White * 0.75f, TextScale);
            currentY += lineH;
        }

        // Current building line — left aligned so words grow rightward, full brightness
        float leftEdge = videoRect.X + (videoRect.Width - actualMaxWidth) / 2f;
        foreach (string line in bottomLines)
        {
            Utils.DrawBorderString(spriteBatch, line,
                new Vector2(leftEdge, currentY), Color.White, TextScale);
            currentY += lineH;
        }
    }

    /// <summary>
    /// Returns words from the block whose timestamp is at or before currentTime,
    /// joined into a string. Grows word-by-word as the video plays.
    /// </summary>
    private static string BuildCurrentLine(CaptionBlock block, float currentTime) => string.Join(" ", block.Words.Where(w => w.Timestamp <= currentTime).Select(w => w.Text));

    /// <summary>
    /// Binary search for the caption block active at currentTime.
    /// Returns null if no block is active (e.g. during a pause in speech).
    /// </summary>
    private CaptionBlock FindActive(float currentTime)
    {
        int lo = 0, hi = _captions.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            var block = _captions[mid];

            if (block.StartTime > currentTime)
                hi = mid - 1;
            else if (block.EndTime <= currentTime)
                lo = mid + 1;
            else
                return block;
        }
        return null;
    }

    /// <summary>
    /// Word-wraps text to fit within maxWidth at the given scale.
    /// </summary>
    private static List<string> WrapText(DynamicSpriteFont font, string text, float maxWidth, float scale)
    {
        List<string> result = [];
        string[] words = WhitespaceRegex().Split(text.Trim());
        string current = "";

        foreach (string word in words)
        {
            string test = string.IsNullOrEmpty(current) ? word : current + " " + word;
            float width = font.MeasureString(test).X * scale;

            if (width > maxWidth && !string.IsNullOrEmpty(current))
            {
                result.Add(current);
                current = word;
            }
            else
                current = test;
        }

        if (!string.IsNullOrEmpty(current))
            result.Add(current);

        return result;
    }
}