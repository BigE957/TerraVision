using Humanizer;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Terraria;
using Terraria.GameContent.UI.Elements;
using Terraria.GameInput;
using Terraria.UI;
using TerraVision.Content.Tiles.TVs;
using TerraVision.Core.VideoPlayer;
using TerraVision.UI.VideoPlayer;

namespace TerraVision.UI;

public class DraggableUIPanel : UIPanel
{
    private Vector2 offset;
    private bool dragging;
    public Func<bool> ShouldDrag;

    public override void LeftMouseDown(UIMouseEvent evt)
    {
        base.LeftMouseDown(evt);
        if (ShouldDrag == null || ShouldDrag())
            DragStart(evt);
    }

    public override void LeftMouseUp(UIMouseEvent evt)
    {
        base.LeftMouseUp(evt);
        if (dragging)
            DragEnd(evt);
    }

    private void DragStart(UIMouseEvent evt)
    {
        offset = new Vector2(evt.MousePosition.X - Left.Pixels, evt.MousePosition.Y - Top.Pixels);
        dragging = true;
    }

    private void DragEnd(UIMouseEvent evt)
    {
        Vector2 endMousePosition = evt.MousePosition;
        dragging = false;

        Left.Set(endMousePosition.X - offset.X, 0f);
        Top.Set(endMousePosition.Y - offset.Y, 0f);

        Recalculate();
    }

    public override void Update(GameTime gameTime)
    {
        base.Update(gameTime);

        if (IsMouseHovering)
        {
            Main.LocalPlayer.mouseInterface = true;
        }

        if (dragging)
        {
            Left.Set(Main.mouseX - offset.X, 0f);
            Top.Set(Main.mouseY - offset.Y, 0f);
            Recalculate();
        }

        var parentSpace = Parent.GetDimensions().ToRectangle();
        if (!GetDimensions().ToRectangle().Intersects(parentSpace))
        {
            Left.Pixels = Utils.Clamp(Left.Pixels, 0, parentSpace.Right - Width.Pixels);
            Top.Pixels = Utils.Clamp(Top.Pixels, 0, parentSpace.Bottom - Height.Pixels);
            Recalculate();
        }
    }
}

public class ResizeHandle(ExampleVideoPlayerUI parent) : UIPanel
{
    private bool _resizing;
    private Vector2 _startMousePos;
    private Vector2 _startSize;

    public override void LeftMouseDown(UIMouseEvent evt)
    {
        base.LeftMouseDown(evt);
        _resizing = true;
        _startMousePos = new Vector2(Main.mouseX, Main.mouseY);
        _startSize = new Vector2(parent._currentWidth, parent._currentHeight);
    }

    public override void LeftMouseUp(UIMouseEvent evt)
    {
        base.LeftMouseUp(evt);
        _resizing = false;
    }

    public override void Update(GameTime gameTime)
    {
        base.Update(gameTime);

        if (_resizing)
        {
            Vector2 delta = new Vector2(Main.mouseX, Main.mouseY) - _startMousePos;
            float newWidth = _startSize.X + delta.X;
            float newHeight = _startSize.Y + delta.Y;
            parent.ResizePlayer(newWidth, newHeight);
        }

        // Change cursor when hovering
        if (IsMouseHovering)
            Main.LocalPlayer.mouseInterface = true;
    }

    protected override void DrawSelf(SpriteBatch spriteBatch)
    {
        base.DrawSelf(spriteBatch);

        // Draw resize icon (diagonal lines)
        CalculatedStyle dimensions = GetDimensions();
        Vector2 pos = dimensions.Position();

        for (int i = 0; i < 3; i++)
        {
            Vector2 start = pos + new Vector2(15 - i * 5, 5);
            Vector2 end = pos + new Vector2(5, 15 - i * 5);
            // Simple line representation
            spriteBatch.Draw(ExampleVideoUISystem.Background.Value, new Rectangle((int)start.X, (int)start.Y, (int)(end.X - start.X), 2), Color.Gray);
        }
    }
}

public class DraggableTimelineScrubber : UIElement
{
    private bool _dragging;
    private float _lastSeekPosition = -1f;
    private const float SEEK_THRESHOLD = 0.005f;
    private readonly object host = null;

    public DraggableTimelineScrubber(VideoPlayerUIElement player)
    {
        host = player;
    }

    public DraggableTimelineScrubber(MediaPlayerEntity entity)
    {
        host = entity;
    }

    public override void LeftMouseDown(UIMouseEvent evt)
    {
        base.LeftMouseDown(evt);
        _dragging = true;
    }

    public override void LeftMouseUp(UIMouseEvent evt)
    {
        base.LeftMouseUp(evt);
        _dragging = false;
    }

    public override void Update(GameTime gameTime)
    {
        base.Update(gameTime);

        if (_dragging && Parent != null)
        {
            CalculatedStyle parentDims = Parent.GetDimensions();
            float relativeX = Main.mouseX - parentDims.X;
            float percentage = Math.Clamp(relativeX / parentDims.Width, 0f, 1f);

            // Only seek if position changed significantly
            if (Math.Abs(percentage - _lastSeekPosition) > SEEK_THRESHOLD)
            {
                if(host is MediaPlayerEntity entity)
                    entity.player.Seek(percentage);
                else if (host is VideoPlayerUIElement element)
                    element.Seek(percentage);
                _lastSeekPosition = percentage;
            }
        }
        else if (!_dragging)
        {
            _lastSeekPosition = -1f;
        }

        if (IsMouseHovering)
        {
            Main.LocalPlayer.mouseInterface = true;
        }
    }

    protected override void DrawSelf(SpriteBatch spriteBatch)
    {
        CalculatedStyle dimensions = GetDimensions();
        spriteBatch.Draw(ExampleVideoUISystem.Background.Value, dimensions.ToRectangle(), Color.White);
    }
}

public class UIRectangle(Color Color) : UIElement
{
    protected override void DrawSelf(SpriteBatch spriteBatch)
    {
        CalculatedStyle dimensions = GetDimensions();
        if (dimensions.Width > 0 && dimensions.Height > 0)
            spriteBatch.Draw(ExampleVideoUISystem.Background.Value, dimensions.ToRectangle(), Color);
    }
}

/// <summary>
/// Custom text input element based on UITextPrompt pattern
/// </summary>
public class CustomTextInput : UIPanel
{
    public string Text = "";
    internal bool _active = false;
    private readonly int _maxLength = 100;
    private readonly string _placeholder = "Enter text...";

    public CustomTextInput(int maxLength = 100, string placeholder = "Enter text...")
    {
        _maxLength = maxLength;
        _placeholder = placeholder;
        BackgroundColor = new Color(33, 43, 79) * 0.8f;
    }

    public override void LeftMouseDown(UIMouseEvent evt)
    {
        base.LeftMouseDown(evt);
        _active = true;
    }

    public override void Update(GameTime gameTime)
    {
        base.Update(gameTime);

        if (IsMouseHovering)
        {
            Main.LocalPlayer.mouseInterface = true;
        }
    }

    protected override void DrawSelf(SpriteBatch spriteBatch)
    {
        base.DrawSelf(spriteBatch);

        if (_active)
        {
            PlayerInput.WritingText = true;
            Main.instance.HandleIME();

            string newInput = Main.GetInputText(Text);
            if (newInput != Text && newInput.Length <= _maxLength)
            {
                Text = newInput;
            }
        }

        // Draw text
        CalculatedStyle dimensions = GetDimensions();
        Vector2 position = dimensions.Position() + new Vector2(8, 8);

        string displayText = string.IsNullOrEmpty(Text) && !_active ? _placeholder : Text;
        Color textColor = string.IsNullOrEmpty(Text) && !_active ? Color.Gray : Color.White;

        // Add cursor when active
        if (_active && (int)(Main.GlobalTimeWrappedHourly * 2) % 2 == 0)
        {
            displayText += "|";
        }

        Utils.DrawBorderString(spriteBatch, displayText, position, textColor, 0.9f);
    }

    public void Deselect()
    {
        _active = false;
    }
}

