using System;
using System.Collections.Generic;

namespace SA.Core.Geometry
{
    public enum RowAlignment
    {
        /// <summary>Rows start at the left margin.</summary>
        Left,
        /// <summary>Each row is centred horizontally.</summary>
        Centre,
        /// <summary>Spare width in a row is shared out between the gaps; a single item is centred.</summary>
        Distribute,
    }

    public enum ItemAlignment
    {
        /// <summary>Items hang from the top of their row.</summary>
        Top,
        Middle,
        Bottom,
    }

    public sealed class PackItem
    {
        public PackItem(string id, double width, double height)
        {
            if (!(width > 0) || !(height > 0)) throw new ArgumentException("Item '" + id + "' must have a positive size.");
            Id = id ?? throw new ArgumentNullException(nameof(id));
            Width = width;
            Height = height;
        }

        public string Id { get; }
        public double Width { get; }
        public double Height { get; }
    }

    public sealed class PackOptions
    {
        /// <summary>Clear space inside the drawable area on every side.</summary>
        public double Margin { get; set; }
        /// <summary>Horizontal space between items in a row.</summary>
        public double GapX { get; set; }
        /// <summary>Vertical space between rows.</summary>
        public double GapY { get; set; }
        public RowAlignment RowAlignment { get; set; } = RowAlignment.Left;
        public ItemAlignment ItemAlignment { get; set; } = ItemAlignment.Top;
    }

    public sealed class Placement
    {
        internal Placement(string id, Rect2 rect, int row)
        {
            Id = id; Rect = rect; Row = row;
        }

        public string Id { get; }
        public Rect2 Rect { get; }
        public Point2 Centre => Rect.Centre;
        /// <summary>0 for the top row.</summary>
        public int Row { get; }
    }

    public sealed class Unplaced
    {
        internal Unplaced(string id, string reason)
        {
            Id = id; Reason = reason;
        }

        public string Id { get; }
        /// <summary>"larger_than_area" or "no_space".</summary>
        public string Reason { get; }
    }

    public sealed class PackResult
    {
        internal PackResult() { }
        public List<Placement> Placed { get; } = new List<Placement>();
        public List<Unplaced> NotPlaced { get; } = new List<Unplaced>();
        public bool AllPlaced => NotPlaced.Count == 0;
    }

    /// <summary>
    /// Places rectangles (viewports of known size) in rows inside a drawable area, top row first, left to right, in the
    /// given order. Deterministic: the same input always gives the same layout. An item that does not fit in the current
    /// row starts a new row; an item that fits nowhere is reported and the next item is tried.
    /// </summary>
    public static class RectPacker
    {
        public static PackResult Pack(Rect2 area, IReadOnlyList<PackItem> items, PackOptions? options = null)
        {
            var opt = options ?? new PackOptions();
            if (opt.Margin < 0 || opt.GapX < 0 || opt.GapY < 0) throw new ArgumentException("Margin and gaps must not be negative.");
            var inner = area.Deflate(opt.Margin);
            var result = new PackResult();
            const double eps = 1e-9;

            var rows = new List<List<PackItem>>();
            var rowHeights = new List<double>();
            var rowWidths = new List<double>();
            double usedHeight = 0; // height of closed rows including gaps after them

            foreach (var item in items)
            {
                if (item.Width > inner.Width + eps || item.Height > inner.Height + eps)
                {
                    result.NotPlaced.Add(new Unplaced(item.Id, "larger_than_area"));
                    continue;
                }

                int r = rows.Count - 1;
                if (r >= 0)
                {
                    double newWidth = rowWidths[r] + opt.GapX + item.Width;
                    double newHeight = Math.Max(rowHeights[r], item.Height);
                    if (newWidth <= inner.Width + eps && usedHeight + newHeight <= inner.Height + eps)
                    {
                        rows[r].Add(item);
                        rowWidths[r] = newWidth;
                        rowHeights[r] = newHeight;
                        continue;
                    }
                }

                double top = r >= 0 ? usedHeight + rowHeights[r] + opt.GapY : 0;
                if (top + item.Height <= inner.Height + eps)
                {
                    if (r >= 0) usedHeight = top;
                    rows.Add(new List<PackItem> { item });
                    rowWidths.Add(item.Width);
                    rowHeights.Add(item.Height);
                }
                else
                {
                    result.NotPlaced.Add(new Unplaced(item.Id, "no_space"));
                }
            }

            double y = inner.MaxY;
            for (int r = 0; r < rows.Count; r++)
            {
                var row = rows[r];
                double h = rowHeights[r];
                double spare = inner.Width - rowWidths[r];
                double x = inner.MinX;
                double gap = opt.GapX;
                switch (opt.RowAlignment)
                {
                    case RowAlignment.Centre: x += spare / 2; break;
                    case RowAlignment.Distribute:
                        if (row.Count > 1) gap += spare / (row.Count - 1);
                        else x += spare / 2;
                        break;
                }
                foreach (var item in row)
                {
                    double iy;
                    switch (opt.ItemAlignment)
                    {
                        case ItemAlignment.Middle: iy = y - h / 2 - item.Height / 2; break;
                        case ItemAlignment.Bottom: iy = y - h; break;
                        default: iy = y - item.Height; break;
                    }
                    result.Placed.Add(new Placement(item.Id, new Rect2(x, iy, item.Width, item.Height), r));
                    x += item.Width + gap;
                }
                y -= h + opt.GapY;
            }
            return result;
        }
    }
}
