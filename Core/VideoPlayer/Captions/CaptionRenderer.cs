using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ReLogic.Graphics;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Terraria;
using Terraria.GameContent;

namespace TerraVision.Core.VideoPlayer.Captions;

public partial class CaptionRenderer
{
    private List<CaptionBlock> _captions = [];

    private const float BaseTextScale = 0.85f;
    private const float PaddingX = 12f;
    private const float PaddingY = 8f;
    private const float BottomMargin = 24f;
    private const float BackgroundAlpha = 0.6f;
    private const float LineSpacing = 2f;

    // Minimum scale before we let content clip rather than making it unreadable.
    // Braille/ASCII art becomes illegible below ~0.3.
    private const float MinScale = 0.3f;

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    public void LoadCaptions(List<CaptionBlock> captions) => _captions = captions;

    public void Clear() => _captions.Clear();

    public void Draw(SpriteBatch spriteBatch, Rectangle videoRect, float currentTime, Texture2D pixel, float opacity)
    {
        if (_captions.Count == 0)
            return;

        CaptionBlock active = FindActive(currentTime);
        if (active == null)
            return;

        var font = FontAssets.MouseText.Value;

        if (active.IsPreformatted)
            DrawPreformatted(spriteBatch, font, videoRect, active, currentTime, pixel, opacity);
        else
            DrawWordWrapped(spriteBatch, font, videoRect, active, currentTime, pixel, opacity);
    }


    private static void DrawPreformatted(SpriteBatch spriteBatch, DynamicSpriteFont font, Rectangle videoRect, CaptionBlock active, float currentTime, Texture2D pixel, float opacity)
    {
        string text = BuildCurrentLine(active, currentTime);
        if (string.IsNullOrEmpty(text))
            return;

        float playerScale = videoRect.Width / 640f;

        string[] lines = text.Split('\n');

        float availW = (videoRect.Width - PaddingX * 4f);
        float availH = (videoRect.Height - PaddingY * 4f - BottomMargin);

        // Measure at base scale — use a non-empty placeholder for blank lines
        // so MeasureString doesn't return zero height and throw off line count.
        float naturalLineH = font.MeasureString("A").Y * BaseTextScale *  + LineSpacing;
        float naturalW = lines.Max(l => font.MeasureString(string.IsNullOrEmpty(l) ? " " : l).X * BaseTextScale);
        float naturalH = lines.Length * naturalLineH;

        // Compute the scale needed to fit width and height, then take the smaller.
        // Clamp to [MinScale, BaseTextScale] — never enlarge, never go illegible.
        float scaleW = naturalW > availW ? (availW * BaseTextScale) / naturalW : BaseTextScale;
        float scaleH = naturalH > availH ? (availH * BaseTextScale) / naturalH : BaseTextScale;
        float scale = MathHelper.Clamp(MathF.Min(scaleW, scaleH), MinScale, BaseTextScale) * playerScale;

        float lineH = font.MeasureString("A").Y * scale + LineSpacing;
        float blockW = lines.Max(l => font.MeasureString(string.IsNullOrEmpty(l) ? " " : l).X * scale);
        float blockH = lines.Length * lineH + PaddingY * 2f;

        float blockX = videoRect.X + (videoRect.Width - blockW) / 2f;
        float blockY = videoRect.Bottom - BottomMargin - blockH;

        // Background
        spriteBatch.Draw(pixel, new Rectangle((int)(blockX - PaddingX), (int)(blockY - PaddingY), (int)(blockW + PaddingX * 2f), (int)blockH), Color.Black * BackgroundAlpha * opacity);

        // Draw each line left-aligned within the block
        float currentY = blockY;
        foreach (string line in lines)
        {
            if (!string.IsNullOrEmpty(line))
                Utils.DrawBorderString(spriteBatch, line, new Vector2(blockX, currentY), Color.White * opacity, scale);
            currentY += lineH;
        }
    }

    private static void DrawWordWrapped(SpriteBatch spriteBatch, DynamicSpriteFont font, Rectangle videoRect, CaptionBlock active, float currentTime, Texture2D pixel, float opacity)
    {
        float playerScale =  videoRect.Width / 640f;
        float scale = BaseTextScale * playerScale;
        float lineH = font.MeasureString("A").Y * scale + LineSpacing;
        float maxWidth = videoRect.Width - (PaddingX * playerScale) * 4f;

        // Bottom: words revealed progressively by timestamp
        string visibleText = BuildCurrentLine(active, currentTime);
        var bottomLines = string.IsNullOrWhiteSpace(visibleText) ? [] : WrapText(font, visibleText, maxWidth, scale);

        // Top: previous completed sentence
        var topLines = string.IsNullOrWhiteSpace(active.PreviousSentence) ? [] : WrapText(font, active.PreviousSentence, maxWidth, scale);

        int totalLines = topLines.Count + bottomLines.Count;
        if (totalLines == 0)
            return;

        // Use FullSentence for stable box width — prevents the box shifting as words appear
        string measureText = !string.IsNullOrWhiteSpace(active.FullSentence) ? active.FullSentence : string.Join(" ", active.Words.Select(w => w.Text));

        var bottomLinesFull = WrapText(font, measureText, maxWidth, scale);
        float stableWidth = bottomLinesFull.Count > 0 ? bottomLinesFull.Max(l => font.MeasureString(l).X * scale) : 0f;

        float actualMaxWidth = topLines.Count > 0 ? MathHelper.Max(stableWidth, topLines.Max(l => font.MeasureString(l).X * scale)) : stableWidth;

        float blockHeight = totalLines * lineH + PaddingY * playerScale * 2f;
        float blockY = videoRect.Bottom - (BottomMargin * videoRect.Width / 1280f) - blockHeight;

        spriteBatch.Draw(pixel, new Rectangle( (int)(videoRect.X + (videoRect.Width - actualMaxWidth) / 2f - PaddingX * playerScale), (int)(blockY - PaddingY * playerScale), (int)(actualMaxWidth + PaddingX * playerScale * 2f), (int)blockHeight), Color.Black * BackgroundAlpha * opacity);

        float currentY = blockY;

        // Previous sentence — slightly dimmed
        foreach (string line in topLines)
        {
            float lineX = videoRect.X + (videoRect.Width - font.MeasureString(line).X * scale) / 2f;
            Utils.DrawBorderString(spriteBatch, line, new Vector2(lineX, currentY), Color.White * 0.75f * opacity, scale);
            currentY += lineH;
        }

        // Current building line — left-aligned so words grow rightward
        float leftEdge = videoRect.X + (videoRect.Width - actualMaxWidth) / 2f;
        foreach (string line in bottomLines)
        {
            Utils.DrawBorderString(spriteBatch, line, new Vector2(leftEdge, currentY), Color.White * opacity, scale);
            currentY += lineH;
        }
    }

    /// <summary>
    /// Returns the visible caption text for the current time.
    /// Preformatted blocks: returns FullSentence directly (preserving newlines).
    /// Word-timed blocks: returns only the words whose timestamp has been reached.
    /// </summary>
    private static string BuildCurrentLine(CaptionBlock block, float currentTime)
    {
        if (block.IsPreformatted)
            return block.FullSentence ?? string.Join("\n", block.Words.Select(w => w.Text));

        return string.Join(" ", block.Words.Where(w => w.Timestamp <= currentTime).Select(w => w.Text));
    }

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
    /// Respects hard newlines — each \n-delimited segment is wrapped independently,
    /// preserving intentional line breaks in manually authored captions.
    /// </summary>
    private static List<string> WrapText(DynamicSpriteFont font, string text, float maxWidth, float scale)
    {
        List<string> result = [];

        foreach (string segment in text.Split('\n'))
        {
            string[] words = WhitespaceRegex().Split(segment.Trim());
            string current = "";

            foreach (string word in words)
            {
                if (string.IsNullOrEmpty(word)) continue;
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
        }

        return result;
    }
}