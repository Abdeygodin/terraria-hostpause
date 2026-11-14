using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ReLogic.Content;
using Terraria;
using Terraria.ModLoader;

namespace HostPause.Common
{
    // The gray frozen frame: the first paused frame is drawn normally, read back once,
    // desaturated and dimmed; later frames skip Main.DoDraw and draw that texture with the banner.
    // Skipping DoDraw also hides the inventory and other UI, so nothing can be changed while paused.
    internal static class PauseVisuals
    {
        static Texture2D frozen;
        static Asset<Texture2D> banner;
        static bool disabled;

        public static void Reset()
        {
            if (frozen == null) return;
            Texture2D texture = frozen;
            frozen = null;
            texture.Dispose();
        }

        public static void Unload()
        {
            Reset();
            banner = null;
            disabled = false;
        }

        public static bool DrawCached(string caption, bool emphasize)
        {
            if (disabled || frozen == null) return false;
            try
            {
                GraphicsDevice device = Main.instance.GraphicsDevice;
                PresentationParameters parameters = device.PresentationParameters;
                // The window was resized: draw one live frame and capture it again.
                if (frozen.Width != parameters.BackBufferWidth || frozen.Height != parameters.BackBufferHeight) { Reset(); return false; }
                Draw(caption, emphasize);
                return true;
            }
            catch (Exception ex) { Fail(ex); return false; }
        }

        public static void CaptureAndDraw(string caption, bool emphasize)
        {
            if (disabled) return;
            try
            {
                if (frozen == null)
                {
                    GraphicsDevice device = Main.instance.GraphicsDevice;
                    PresentationParameters parameters = device.PresentationParameters;
                    int width = parameters.BackBufferWidth, height = parameters.BackBufferHeight;
                    var pixels = new Color[checked(width * height)];
                    device.GetBackBufferData(pixels);
                    for (int i = 0; i < pixels.Length; i++)
                    {
                        Color c = pixels[i];
                        byte gray = (byte)(((c.R * 54 + c.G * 183 + c.B * 19) >> 8) * 0.70f);
                        pixels[i] = new Color(gray, gray, gray, (byte)255);
                    }
                    var texture = new Texture2D(device, width, height);
                    texture.SetData(pixels);
                    frozen = texture;
                }
                Draw(caption, emphasize);
            }
            catch (Exception ex) { Fail(ex); }
        }

        static void Draw(string caption, bool emphasize)
        {
            GraphicsDevice device = Main.instance.GraphicsDevice;
            SpriteBatch batch = Main.spriteBatch;
            banner ??= ModContent.Request<Texture2D>("HostPause/Assets/Banner", AssetRequestMode.ImmediateLoad);
            Texture2D bannerTexture = banner.Value;
            PresentationParameters parameters = device.PresentationParameters;
            int width = parameters.BackBufferWidth, height = parameters.BackBufferHeight;
            Viewport previous = device.Viewport;
            device.SetRenderTarget(null);
            device.Viewport = new Viewport(0, 0, width, height);
            bool begun = false;
            try
            {
                batch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp, DepthStencilState.None, RasterizerState.CullNone);
                begun = true;
                batch.Draw(frozen, new Rectangle(0, 0, width, height), Color.White);
                // Same proportions as the vanilla HostPause banner.
                float aspect = bannerTexture.Width / (float)bannerTexture.Height;
                int bw = (int)(Math.Min(width * 0.68f, height * 0.38f * aspect) / 3f);
                int bh = (int)(bw / aspect);
                int x = (width - bw) / 2, y = (height - bh) / 2;
                batch.Draw(bannerTexture, new Rectangle(x, y, bw, bh), Color.White);
                float scale = Math.Clamp(width / 1600f, 0.75f, 1.15f) * (emphasize ? 1.6f : 1f);
                Utils.DrawBorderString(batch, caption, new Vector2(width / 2f, y + bh + 14f), Color.White, scale, 0.5f, 0f);
            }
            finally
            {
                if (begun) batch.End();
                device.Viewport = previous;
            }
        }

        // Rendering is optional. A GPU or readback failure must not resume this client's
        // gameplay while the server and the other players stay frozen.
        static void Fail(Exception ex)
        {
            disabled = true;
            Reset();
            PauseSystem.VisualFault(ex);
        }
    }
}
