using System;
using System.IO;
using System.Reflection;

namespace HostPause
{
    // Uses the game's own FNA/XNA objects without depending on one platform's graphics assembly.
    // Readback and desaturation happen once per pause; subsequent frames just draw two textures.
    public static class Visuals
    {
        const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        static Type main, textureType, rectangleType, colorType, vectorType;
        static object frozen, banner;
        static int capturedWidth, capturedHeight, bannerWidth, bannerHeight;
        static bool drawn;
        static MethodInfo drawRect, begin, end, text;

        public static void Init(Type mainType)
        {
            main = mainType;
            text = main.Assembly.GetType("Terraria.Utils").GetMethod("DrawBorderString", Flags);
            Type batchType = text.GetParameters()[0].ParameterType;
            textureType = batchType.Assembly.GetType("Microsoft.Xna.Framework.Graphics.Texture2D", true);
            colorType = text.GetParameters()[3].ParameterType;
            vectorType = text.GetParameters()[2].ParameterType;
            foreach (MethodInfo method in batchType.GetMethods())
            {
                ParameterInfo[] p = method.GetParameters();
                if (method.Name == "Draw" && p.Length == 3 && p[0].ParameterType == textureType && p[1].ParameterType.FullName == "Microsoft.Xna.Framework.Rectangle" && p[2].ParameterType == colorType)
                { drawRect = method; rectangleType = p[1].ParameterType; break; }
            }
            begin = batchType.GetMethod("Begin", Type.EmptyTypes);
            end = batchType.GetMethod("End", Type.EmptyTypes);
        }

        static object Field(string name) { return main.GetField(name, Flags).GetValue(null); }
        static object Device { get { object game = Field("instance"); return game.GetType().GetProperty("GraphicsDevice").GetValue(game, null); } }
        static int PropertyInt(object obj, string name) { return (int)obj.GetType().GetProperty(name).GetValue(obj, null); }
        static MethodInfo ArrayMethod(Type type, string name)
        {
            foreach (MethodInfo method in type.GetMethods())
                if (method.Name == name && method.IsGenericMethodDefinition && method.GetParameters().Length == 1 && method.GetParameters()[0].ParameterType.IsArray)
                    return method.MakeGenericMethod(typeof(byte));
            throw new MissingMethodException(type.FullName, name);
        }

        public static void Reset()
        {
            if (frozen != null) { ((IDisposable)frozen).Dispose(); frozen = null; }
            drawn = false;
        }

        public static bool DrawCached(string key)
        {
            if (frozen == null) return false;
            Draw(key);
            drawn = true;
            return true;
        }

        public static void CaptureAndDraw(string key)
        {
            if (drawn) { drawn = false; return; }
            object device = Device;
            object presentation = device.GetType().GetProperty("PresentationParameters").GetValue(device, null);
            int width = PropertyInt(presentation, "BackBufferWidth"), height = PropertyInt(presentation, "BackBufferHeight");
            if (frozen != null && (width != capturedWidth || height != capturedHeight)) Reset();
            if (frozen == null)
            {
                byte[] pixels = new byte[checked(width * height * 4)];
                ArrayMethod(device.GetType(), "GetBackBufferData").Invoke(device, new object[] { pixels });
                for (int i = 0; i < pixels.Length; i += 4)
                {
                    byte gray = (byte)(((pixels[i] * 54 + pixels[i + 1] * 183 + pixels[i + 2] * 19) >> 8) * 0.70);
                    pixels[i] = pixels[i + 1] = pixels[i + 2] = gray;
                    pixels[i + 3] = 255;
                }
                frozen = Activator.CreateInstance(textureType, new object[] { device, width, height });
                ArrayMethod(textureType, "SetData").Invoke(frozen, new object[] { pixels });
                capturedWidth = width; capturedHeight = height;
                if (banner == null)
                {
                    string path = Path.Combine(Path.GetDirectoryName(main.Assembly.Location), "HostPause.banner.png");
                    MethodInfo fromStream = null;
                    foreach (MethodInfo method in textureType.GetMethods())
                        if (method.Name == "FromStream" && method.GetParameters().Length == 2) { fromStream = method; break; }
                    using (Stream stream = File.OpenRead(path)) banner = fromStream.Invoke(null, new object[] { device, stream });
                    bannerWidth = PropertyInt(banner, "Width"); bannerHeight = PropertyInt(banner, "Height");
                }
            }
            Draw(key);
        }

        static object Rectangle(int x, int y, int width, int height) { return Activator.CreateInstance(rectangleType, new object[] { x, y, width, height }); }

        static void Draw(string key)
        {
            object device = Device, batch = Field("spriteBatch");
            PropertyInfo viewportProperty = device.GetType().GetProperty("Viewport");
            object previousViewport = viewportProperty.GetValue(device, null);
            object presentation = device.GetType().GetProperty("PresentationParameters").GetValue(device, null);
            int width = PropertyInt(presentation, "BackBufferWidth"), height = PropertyInt(presentation, "BackBufferHeight");
            object fullViewport = Activator.CreateInstance(viewportProperty.PropertyType, new object[] { 0, 0, width, height });
            viewportProperty.SetValue(device, fullViewport, null);
            bool begun = false;
            try
            {
                begin.Invoke(batch, null); begun = true;
                object white = Activator.CreateInstance(colorType, new object[] { 255, 255, 255, 255 });
                drawRect.Invoke(batch, new object[] { frozen, Rectangle(0, 0, width, height), white });
                float factor = Math.Min(width * 0.68f / bannerWidth, height * 0.38f / bannerHeight) / 3f;
                int bw = (int)(bannerWidth * factor), bh = (int)(bannerHeight * factor);
                int x = (width - bw) / 2, y = (height - bh) / 2;
                drawRect.Invoke(batch, new object[] { banner, Rectangle(x, y, bw, bh), white });
                object position = Activator.CreateInstance(vectorType, new object[] { width / 2f, y + bh + 14f });
                text.Invoke(null, new object[] { batch, "Хост: " + key + " — продолжить игру", position, white, Math.Max(0.75f, Math.Min(width / 1600f, 1.15f)), 0.5f, 0f, -1 });
            }
            finally
            {
                if (begun) end.Invoke(batch, null);
                viewportProperty.SetValue(device, previousViewport, null);
            }
        }
    }
}
