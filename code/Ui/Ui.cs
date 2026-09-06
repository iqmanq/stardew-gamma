using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Menus;

namespace Gamma.Ui;

/// <summary>Small drawing helpers that reuse the game's own menu textures and fonts.</summary>
public static class VanillaUi
{
    /// <summary>The wooden menu/dialogue box sprite in the game's shared cursor sheet.</summary>
    public static readonly Rectangle BoxSource = new(384, 37, 18, 18);

    /// <summary>
    /// The 9-slice box sprite has a transparent middle, so an opaque dark interior is painted
    /// under the frame — otherwise the game world shows straight through menus.
    /// </summary>
    private static readonly Color MenuFill = new Color(26, 19, 15) * 0.94f;

    public static int LineHeight => (int)Game1.smallFont.MeasureString("Ag").Y + 6;

    /// <summary>Draws a 9-slice menu box using the vanilla menu texture (same as IClickableMenu.drawTextureBox).</summary>
    public static void DrawBox(SpriteBatch b, int x, int y, int width, int height)
    {
        if (width > 32 && height > 32)
            b.Draw(Game1.staminaRect, new Rectangle(x + 16, y + 16, width - 32, height - 32),
                null, MenuFill, 0f, Vector2.Zero, SpriteEffects.None, 1f);
        IClickableMenu.drawTextureBox(b, Game1.mouseCursors, BoxSource, x, y, width, height, Color.White, 1f, true, 1f);
    }

    /// <summary>Greedy word-wrap for menu text.</summary>
    public static List<string> Wrap(SpriteFont font, string text, int maxWidth)
    {
        var lines = new List<string>();
        if (text == null) return lines;
        string current = "";
        foreach (var word in text.Split(' '))
        {
            string test = current.Length == 0 ? word : current + " " + word;
            if (current.Length > 0 && font.MeasureString(test).X > maxWidth)
            {
                lines.Add(current);
                current = word;
            }
            else
            {
                current = test;
            }
        }
        lines.Add(current);
        return lines;
    }
}
