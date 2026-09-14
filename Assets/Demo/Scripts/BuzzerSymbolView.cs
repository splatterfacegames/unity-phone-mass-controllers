using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Splatter.Pmc.Demo
{
    /// <summary>
    /// Draws one Buzzer Party symbol (procedural shapes, no textures) with a small pop when it
    /// changes. Port of demo/symbol_view.gd — a uGUI Graphic that triangulates the same outlines.
    /// </summary>
    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class BuzzerSymbolView : Graphic
    {
        private string _shape = "";
        private Color _shapeColor = Color.white;
        private float _pop = 1f;

        /// <summary>Show a shape ("circle", "square", …, "ring") in a color, popping in.</summary>
        public void ShowSymbol(string shape, Color color, bool animate = true)
        {
            if (shape == _shape && color == _shapeColor)
                return;
            _shape = shape;
            _shapeColor = color;
            _pop = animate ? 0.6f : 1f;
            SetVerticesDirty();
        }

        /// <summary>Draw nothing.</summary>
        public void ClearSymbol()
        {
            _shape = "";
            SetVerticesDirty();
        }

        private void Update()
        {
            if (_pop < 1f)
            {
                _pop = Mathf.Min(1f, _pop + Time.deltaTime * 5f);
                SetVerticesDirty();
            }
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            if (_shape == "")
                return;
            Rect r = rectTransform.rect;
            Vector2 c = r.center;
            float easePop = 1f + Mathf.Sin(Mathf.Clamp01((_pop - 0.6f) / 0.4f) * Mathf.PI) * 0.12f;
            float rad = Mathf.Min(r.width, r.height) * 0.42f * easePop * Mathf.Clamp01(_pop + 0.2f);
            if (rad <= 1f)
                return;
            // Soft shadow first (drawn below the shape on screen), then the shape.
            EmitShape(vh, _shape, c + new Vector2(0f, -rad * 0.08f), rad, new Color(0f, 0f, 0f, 0.28f));
            EmitShape(vh, _shape, c, rad, _shapeColor);
        }

        private static void EmitShape(VertexHelper vh, string shape, Vector2 c, float r, Color32 col)
        {
            switch (shape)
            {
                case "circle":
                    EmitFan(vh, c, r, col, 48);
                    return;
                case "ring":
                    EmitRing(vh, c, r * 0.96f, r * 0.60f, col, 96);
                    return;
                case "square":
                    EmitPoly(vh, RoundedRect(new Rect(c - new Vector2(r, r) * 0.88f, new Vector2(r, r) * 1.76f), r * 0.2f), col);
                    return;
                case "triangle":
                    EmitPoly(vh, Regular(c + new Vector2(0f, r * 0.15f), r * 1.05f, 3, Mathf.PI / 2f), col);
                    return;
                case "diamond":
                    EmitPoly(vh, new List<Vector2> {
                        c + new Vector2(0f, r), c + new Vector2(r * 0.82f, 0f),
                        c + new Vector2(0f, -r), c + new Vector2(-r * 0.82f, 0f),
                    }, col);
                    return;
                case "hexagon":
                    EmitPoly(vh, Regular(c, r, 6, 0f), col);
                    return;
                case "star":
                {
                    var pts = new List<Vector2>();
                    for (int i = 0; i < 10; i++)
                    {
                        float a = Mathf.PI / 2f + i * Mathf.PI / 5f;
                        pts.Add(c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * (i % 2 == 0 ? r : r * 0.45f));
                    }
                    EmitPoly(vh, pts, col);
                    return;
                }
                case "heart":
                {
                    var pts = new List<Vector2>();
                    for (int i = 0; i < 64; i++)
                    {
                        float t = Mathf.PI * 2f * i / 64f;
                        float x = 16f * Mathf.Pow(Mathf.Sin(t), 3f);
                        float y = 13f * Mathf.Cos(t) - 5f * Mathf.Cos(2f * t) - 2f * Mathf.Cos(3f * t) - Mathf.Cos(4f * t);
                        pts.Add(c + new Vector2(x, y) * (r / 16f) + new Vector2(0f, r * 0.05f));
                    }
                    EmitPoly(vh, pts, col);
                    return;
                }
            }
        }

        private static void EmitPoly(VertexHelper vh, IList<Vector2> pts, Color32 col)
        {
            List<int> tris = PolyTri.Triangulate(pts);
            if (tris.Count == 0)
                return;
            int baseIdx = vh.currentVertCount;
            for (int i = 0; i < pts.Count; i++)
                vh.AddVert(pts[i], col, Vector2.zero);
            for (int i = 0; i + 2 < tris.Count; i += 3)
                vh.AddTriangle(baseIdx + tris[i], baseIdx + tris[i + 1], baseIdx + tris[i + 2]);
        }

        private static void EmitFan(VertexHelper vh, Vector2 c, float r, Color32 col, int segs)
        {
            int baseIdx = vh.currentVertCount;
            vh.AddVert(c, col, Vector2.zero);
            for (int i = 0; i < segs; i++)
            {
                float a = Mathf.PI * 2f * i / segs;
                vh.AddVert(c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r, col, Vector2.zero);
            }
            for (int i = 0; i < segs; i++)
                vh.AddTriangle(baseIdx, baseIdx + 1 + i, baseIdx + 1 + (i + 1) % segs);
        }

        private static void EmitRing(VertexHelper vh, Vector2 c, float rOuter, float rInner, Color32 col, int segs)
        {
            int baseIdx = vh.currentVertCount;
            for (int i = 0; i < segs; i++)
            {
                float a = Mathf.PI * 2f * i / segs;
                var dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                vh.AddVert(c + dir * rOuter, col, Vector2.zero);
                vh.AddVert(c + dir * rInner, col, Vector2.zero);
            }
            for (int i = 0; i < segs; i++)
            {
                int o0 = baseIdx + i * 2, i0 = o0 + 1;
                int o1 = baseIdx + ((i + 1) % segs) * 2, i1 = o1 + 1;
                vh.AddTriangle(o0, i0, o1);
                vh.AddTriangle(i0, i1, o1);
            }
        }

        private static List<Vector2> Regular(Vector2 c, float r, int n, float start)
        {
            var pts = new List<Vector2>(n);
            for (int i = 0; i < n; i++)
            {
                float a = start + Mathf.PI * 2f * i / n;
                pts.Add(c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r);
            }
            return pts;
        }

        private static List<Vector2> RoundedRect(Rect rect, float rad)
        {
            var pts = new List<Vector2>();
            var corners = new[]
            {
                new KeyValuePair<Vector2, float>(rect.position + new Vector2(rect.size.x - rad, rad), -Mathf.PI / 2f),
                new KeyValuePair<Vector2, float>(rect.position + rect.size - new Vector2(rad, rad), 0f),
                new KeyValuePair<Vector2, float>(rect.position + new Vector2(rad, rect.size.y - rad), Mathf.PI / 2f),
                new KeyValuePair<Vector2, float>(rect.position + new Vector2(rad, rad), Mathf.PI),
            };
            foreach (var corner in corners)
            {
                for (int i = 0; i < 7; i++)
                {
                    float a = corner.Value + (Mathf.PI / 2f) * i / 6f;
                    pts.Add(corner.Key + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * rad);
                }
            }
            return pts;
        }
    }

    /// <summary>Ear-clipping triangulation for the simple polygons above. O(n²), tiny n.</summary>
    internal static class PolyTri
    {
        public static List<int> Triangulate(IList<Vector2> poly)
        {
            var tris = new List<int>();
            int n = poly.Count;
            if (n < 3)
                return tris;
            var v = new List<int>(n);
            for (int i = 0; i < n; i++)
                v.Add(i);
            if (SignedArea(poly) < 0f)
                v.Reverse(); // ears need counter-clockwise winding

            int guard = 0;
            while (v.Count > 3 && guard++ < 10000)
            {
                bool clipped = false;
                for (int i = 0; i < v.Count; i++)
                {
                    int i0 = v[(i + v.Count - 1) % v.Count], i1 = v[i], i2 = v[(i + 1) % v.Count];
                    Vector2 a = poly[i0], b = poly[i1], c = poly[i2];
                    if (Cross(b - a, c - b) <= 0f)
                        continue; // reflex or degenerate
                    bool inside = false;
                    for (int j = 0; j < v.Count; j++)
                    {
                        int k = v[j];
                        if (k == i0 || k == i1 || k == i2)
                            continue;
                        if (PointInTri(poly[k], a, b, c))
                        {
                            inside = true;
                            break;
                        }
                    }
                    if (inside)
                        continue;
                    tris.Add(i0);
                    tris.Add(i1);
                    tris.Add(i2);
                    v.RemoveAt(i);
                    clipped = true;
                    break;
                }
                if (!clipped)
                {
                    // Defensive fallback for a degenerate/self-intersecting polygon: fan it.
                    for (int i = 1; i + 1 < v.Count; i++)
                    {
                        tris.Add(v[0]);
                        tris.Add(v[i]);
                        tris.Add(v[i + 1]);
                    }
                    return tris;
                }
            }
            if (v.Count == 3)
            {
                tris.Add(v[0]);
                tris.Add(v[1]);
                tris.Add(v[2]);
            }
            return tris;
        }

        private static float SignedArea(IList<Vector2> p)
        {
            float a = 0f;
            for (int i = 0; i < p.Count; i++)
            {
                Vector2 u = p[i], w = p[(i + 1) % p.Count];
                a += u.x * w.y - w.x * u.y;
            }
            return a * 0.5f;
        }

        private static float Cross(Vector2 u, Vector2 w)
        {
            return u.x * w.y - u.y * w.x;
        }

        private static bool PointInTri(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float d1 = Cross(b - a, p - a), d2 = Cross(c - b, p - b), d3 = Cross(a - c, p - c);
            return d1 >= 0f && d2 >= 0f && d3 >= 0f;
        }
    }
}
