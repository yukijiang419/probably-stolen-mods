using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace profit_help
{
    /// <summary>
    /// A small red dot in the top-right corner of every item marked 禁售 (by this mod or by Shop Assist),
    /// wherever it is: bag, counter, display case, hidden stash, vending machines and inside open containers.
    /// Items that are only blocked because their container is 禁售 get no dot of their own; the container
    /// shows it. Drawn like the deal frames: a child of the item's own UI node, never catching clicks.
    /// </summary>
    internal static class NoSaleBadges
    {
        private const string BadgeName = "ProfitHelperNoSaleBadge";
        private const float Size = 8f;
        private const float Inset = 1f;

        private static readonly Dictionary<GameItemElement, GameObject> badges = new Dictionary<GameItemElement, GameObject>();
        private static Sprite dot;

        /// <summary>Called on every refresh while a store is loaded.</summary>
        public static void Refresh()
        {
            var wanted = new HashSet<GameItemElement>();
            foreach (GameItem item in EmporiumEntry.Instance.GetAllItems())
            {
                if (item is GameItemElement element && !element.IsDestroyed() && element.middle != null && NoSale.IsNoSale(item))
                {
                    wanted.Add(element);
                }
            }
            var stale = new List<GameItemElement>();
            foreach (KeyValuePair<GameItemElement, GameObject> pair in badges)
            {
                if (!wanted.Contains(pair.Key) || pair.Value == null)
                {
                    stale.Add(pair.Key);
                }
            }
            foreach (GameItemElement element in stale)
            {
                if (badges[element] != null)
                {
                    Object.Destroy(badges[element]);
                }
                badges.Remove(element);
            }
            foreach (GameItemElement element in wanted)
            {
                if (!badges.TryGetValue(element, out GameObject badge))
                {
                    badge = Create(element);
                    badges[element] = badge;
                }
                Place(badge, element);
            }
        }

        /// <summary>Leaving the store: the badges went with the scene, only forget them.</summary>
        public static void Forget()
        {
            foreach (GameObject badge in badges.Values)
            {
                if (badge != null)
                {
                    Object.Destroy(badge);
                }
            }
            badges.Clear();
        }

        private static GameObject Create(GameItemElement element)
        {
            var badge = new GameObject(BadgeName, typeof(RectTransform), typeof(Image));
            var rect = badge.GetComponent<RectTransform>();
            rect.SetParent(element.middle.rectTransform, false);
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(1f, 1f);
            rect.sizeDelta = new Vector2(Size, Size);
            var image = badge.GetComponent<Image>();
            image.sprite = Dot();
            image.raycastTarget = false;
            return badge;
        }

        /// <summary>Top-right corner of the item's footprint; kept on top of the item's own sprites.</summary>
        private static void Place(GameObject badge, GameItemElement element)
        {
            var rect = badge.GetComponent<RectTransform>();
            rect.anchoredPosition = new Vector2(element.widthPixels - Inset, -Inset);
            rect.SetAsLastSibling();
        }

        /// <summary>A red disc with a dark rim, made once at runtime.</summary>
        private static Sprite Dot()
        {
            if (dot != null)
            {
                return dot;
            }
            const int n = 16;
            var texture = new Texture2D(n, n, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
            var red = new Color(0.93f, 0.16f, 0.16f, 1f);
            var rim = new Color(0.25f, 0.02f, 0.02f, 1f);
            float c = (n - 1) / 2f;
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
                    Color color = d <= c - 2f ? red : d <= c ? rim : Color.clear;
                    texture.SetPixel(x, y, color);
                }
            }
            texture.Apply();
            dot = Sprite.Create(texture, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f));
            return dot;
        }
    }
}
