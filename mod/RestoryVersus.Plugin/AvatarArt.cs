using System.Collections.Generic;
using UnityEngine;

namespace RestoryVersus
{
    // Player avatars: 16 little retro robots drawn in code. The game's ProfilesInfo holds a single icon in
    // this build and the dialogue portraits are not loaded in the main menu, where players pick avatars, so
    // the mod draws its own. Same id -> same picture on both copies; only the id travels.
    internal static class AvatarArt
    {
        public const int Count = 16;
        private const int Size = 128;
        private static readonly Dictionary<string, Sprite> cache = new Dictionary<string, Sprite>();

        private static readonly Color32[] Backgrounds =
        {
            new Color32(255, 139, 61, 255), new Color32(84, 210, 132, 255), new Color32(110, 170, 255, 255), new Color32(236, 92, 92, 255),
            new Color32(186, 120, 255, 255), new Color32(255, 205, 64, 255), new Color32(64, 200, 210, 255), new Color32(255, 120, 190, 255),
        };

        public static IEnumerable<string> Ids()
        {
            for (int i = 0; i < Count; i++) yield return "robot-" + i;
        }

        public static Sprite Get(string id)
        {
            if (string.IsNullOrEmpty(id) || !id.StartsWith("robot-")) return null;
            if (!int.TryParse(id.Substring(6), out int n) || n < 0 || n >= Count) return null;
            if (cache.TryGetValue(id, out var s) && s != null) return s;
            s = Draw(n);
            cache[id] = s;
            return s;
        }

        // Variant n: background color n % 8, eyes n % 4 (dots, squares, happy arcs, visor),
        // mouth (n / 4) % 4 (smile, flat, open, grill), antenna for odd n.
        private static Sprite Draw(int n)
        {
            var bg = (Color)Backgrounds[n % Backgrounds.Length];
            var head = new Color(0.95f, 0.93f, 0.89f, 1f);
            var ink = new Color(0.13f, 0.14f, 0.2f, 1f);
            int eyes = n % 4, mouth = (n / 4) % 4;
            bool antenna = n % 2 == 1;

            var px = new Color32[Size * Size];
            for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                // Coordinates in [-1, 1], y up.
                var p = new Vector2((x + 0.5f) / Size * 2f - 1f, (y + 0.5f) / Size * 2f - 1f);
                float aa = 2f / Size;
                Color c = bg;

                // Head and antenna.
                float dHead = RoundBox(p - new Vector2(0f, -0.08f), new Vector2(0.62f, 0.5f), 0.18f);
                float dEarL = RoundBox(p - new Vector2(-0.7f, -0.08f), new Vector2(0.07f, 0.17f), 0.05f);
                float dEarR = RoundBox(p - new Vector2(0.7f, -0.08f), new Vector2(0.07f, 0.17f), 0.05f);
                float dHeadAll = Mathf.Min(dHead, Mathf.Min(dEarL, dEarR));
                if (antenna)
                {
                    dHeadAll = Mathf.Min(dHeadAll, Segment(p, new Vector2(0f, 0.42f), new Vector2(0f, 0.68f)) - 0.04f);
                    float dBall = (p - new Vector2(0f, 0.74f)).magnitude - 0.09f;
                    c = Blend(c, bg * 0.6f + Color.white * 0.4f, Cover(dBall, aa));
                }
                c = Blend(c, head, Cover(dHeadAll, aa));

                // Eyes.
                float dEyes = 10f;
                var eL = new Vector2(-0.26f, 0.06f);
                var eR = new Vector2(0.26f, 0.06f);
                switch (eyes)
                {
                    case 0: dEyes = Mathf.Min((p - eL).magnitude - 0.1f, (p - eR).magnitude - 0.1f); break;
                    case 1: dEyes = Mathf.Min(RoundBox(p - eL, new Vector2(0.1f, 0.1f), 0.02f), RoundBox(p - eR, new Vector2(0.1f, 0.1f), 0.02f)); break;
                    case 2: dEyes = Mathf.Min(Arc(p - eL, 0.12f, true), Arc(p - eR, 0.12f, true)) - 0.035f; break;
                    case 3: dEyes = RoundBox(p - new Vector2(0f, 0.06f), new Vector2(0.42f, 0.09f), 0.08f); break;
                }
                c = Blend(c, eyes == 3 ? bg * 0.55f : ink, Cover(dEyes, aa));
                if (eyes == 3)
                {
                    // Visor shine.
                    float dShine = RoundBox(p - new Vector2(-0.22f, 0.09f), new Vector2(0.1f, 0.025f), 0.02f);
                    c = Blend(c, Color.white, Cover(dShine, aa) * 0.8f);
                }

                // Mouth.
                float dMouth = 10f;
                var m = new Vector2(0f, -0.3f);
                switch (mouth)
                {
                    case 0: dMouth = Arc(p - m - new Vector2(0f, 0.1f), 0.2f, false) - 0.035f; break;
                    case 1: dMouth = Segment(p, m + new Vector2(-0.2f, 0f), m + new Vector2(0.2f, 0f)) - 0.035f; break;
                    case 2: dMouth = Mathf.Abs((p - m).magnitude - 0.09f) - 0.035f; break;
                    case 3:
                        dMouth = RoundBox(p - m, new Vector2(0.24f, 0.08f), 0.03f);
                        c = Blend(c, ink, Cover(dMouth, aa));
                        dMouth = 10f;
                        for (int i = -1; i <= 1; i++)
                            dMouth = Mathf.Min(dMouth, RoundBox(p - m - new Vector2(i * 0.12f, 0f), new Vector2(0.02f, 0.06f), 0.01f));
                        c = Blend(c, head, Cover(dMouth, aa));
                        dMouth = 10f;
                        break;
                }
                c = Blend(c, ink, Cover(dMouth, aa));

                px[y * Size + x] = c;
            }

            var tex = new Texture2D(Size, Size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, name = "versus-robot-" + n };
            tex.SetPixels32(px);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, Size, Size), new Vector2(0.5f, 0.5f), 100);
        }

        private static float Cover(float d, float aa) => Mathf.Clamp01(0.5f - d / aa);

        private static Color Blend(Color under, Color over, float a)
        {
            var c = Color.Lerp(under, over, a);
            c.a = 1f;
            return c;
        }

        private static float RoundBox(Vector2 p, Vector2 half, float r)
        {
            var q = new Vector2(Mathf.Abs(p.x) - half.x + r, Mathf.Abs(p.y) - half.y + r);
            return new Vector2(Mathf.Max(q.x, 0f), Mathf.Max(q.y, 0f)).magnitude + Mathf.Min(Mathf.Max(q.x, q.y), 0f) - r;
        }

        private static float Segment(Vector2 p, Vector2 a, Vector2 b)
        {
            var pa = p - a;
            var ba = b - a;
            float h = Mathf.Clamp01(Vector2.Dot(pa, ba) / Vector2.Dot(ba, ba));
            return (pa - ba * h).magnitude;
        }

        // Distance to a half circle of radius r: the upper half (happy eyes) or the lower half (smile).
        private static float Arc(Vector2 p, float r, bool upper)
        {
            bool inHalf = upper ? p.y >= 0f : p.y <= 0f;
            if (inHalf) return Mathf.Abs(p.magnitude - r);
            return Mathf.Min((p - new Vector2(-r, 0f)).magnitude, (p - new Vector2(r, 0f)).magnitude);
        }
    }
}
