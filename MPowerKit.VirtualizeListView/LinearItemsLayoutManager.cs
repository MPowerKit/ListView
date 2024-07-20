﻿namespace MPowerKit.VirtualizeListView;

public class LinearItemsLayoutManager : VirtualizeItemsLayoutManger
{
    #region ItemSpacing
    public double ItemSpacing
    {
        get => (double)GetValue(ItemSpacingProperty);
        set => SetValue(ItemSpacingProperty, value);
    }

    public static readonly BindableProperty ItemSpacingProperty =
        BindableProperty.Create(
            nameof(ItemSpacing),
            typeof(double),
            typeof(LinearItemsLayoutManager));
    #endregion

    protected override Size GetEstimatedItemSize(VirtualizeListViewItem item)
    {
        if (IsOrientation(ScrollOrientation.Both) || item.Position < 0) return new();

        if (IsOrientation(ScrollOrientation.Vertical))
        {
            return new Size(AvailableSpace.Width, 200d);
        }
        else
        {
            return new Size(200d, AvailableSpace.Height);
        }
    }

    protected override Size MeasureItem(IReadOnlyList<VirtualizeListViewItem> items, VirtualizeListViewItem item, Size availableSpace)
    {
        if (IsOrientation(ScrollOrientation.Both)
            || items.Count == 0 || item.Position == -1) return new Size();

        var iview = (item.Cell as IView)!;

        return IsOrientation(ScrollOrientation.Vertical)
            ? iview.Measure(availableSpace.Width, double.PositiveInfinity)
            : iview.Measure(double.PositiveInfinity, availableSpace.Height);
    }

    protected override void ArrangeItem(IReadOnlyList<VirtualizeListViewItem> items, VirtualizeListViewItem item, Size availableSpace)
    {
        var count = items.Count;

        if (IsOrientation(ScrollOrientation.Both)
            || count == 0 || item.Position == -1) return;

        var prevIndex = item.Position - 1;
        var prevItemBounds = prevIndex == -1 ? new() : items[prevIndex].Bounds;

        var margin = GetItemMargin(item);

        if (IsOrientation(ScrollOrientation.Vertical))
        {
            var bottom = prevItemBounds.Bottom;

            var newAvailableSpace = new Size(availableSpace.Width - margin.HorizontalThickness, availableSpace.Height);

            item.Cell!.WidthRequest = newAvailableSpace.Width;
            item.Cell.HeightRequest = AutoSize;

            var request = MeasureItem(items, item, newAvailableSpace);

            item.CellBounds = new Rect(margin.Left, bottom + margin.Top, request.Width, request.Height);
            item.Bounds = new Rect(0d, bottom, request.Width, request.Height + margin.VerticalThickness);
        }
        else
        {
            var right = prevItemBounds.Right;

            var newAvailableSpace = new Size(availableSpace.Width, availableSpace.Height - margin.VerticalThickness);

            item.Cell!.HeightRequest = newAvailableSpace.Height;
            item.Cell.WidthRequest = AutoSize;

            var request = MeasureItem(items, item, newAvailableSpace);

            item.CellBounds = new Rect(right + margin.Left, margin.Top, request.Width, request.Height);
            item.Bounds = new Rect(right, 0d, request.Width + margin.HorizontalThickness, request.Height);
        }
    }

    protected override void ShiftAllItems(IReadOnlyList<VirtualizeListViewItem> items, int start, int exclusiveEnd)
    {
        var count = items.Count;

        if (IsOrientation(ScrollOrientation.Both) || start < 0
            || start >= count || exclusiveEnd <= 0 || exclusiveEnd > count) return;

        var item = items[start];
        var prevIndex = start - 1;
        var prevBounds = prevIndex == -1 ? new() : items[prevIndex].Bounds;

        if (IsOrientation(ScrollOrientation.Vertical))
        {
            var dy = prevBounds.Bottom - item.Bounds.Y;
            if (dy == 0d) return;

            for (int i = start; i < exclusiveEnd; i++)
            {
                var currentItem = items[i];

                currentItem.CellBounds = new Rect(currentItem.CellBounds.X, currentItem.CellBounds.Y + dy, currentItem.CellBounds.Width, currentItem.CellBounds.Height);
                currentItem.Bounds = new Rect(currentItem.Bounds.X, currentItem.Bounds.Y + dy, currentItem.Bounds.Width, currentItem.Bounds.Height);
            }
        }
        else
        {
            var dx = prevBounds.Right - item.Bounds.X;
            if (dx == 0d) return;

            for (int i = start; i < exclusiveEnd; i++)
            {
                var currentItem = items[i];
                currentItem.CellBounds = new Rect(currentItem.CellBounds.X + dx, currentItem.CellBounds.Y, currentItem.CellBounds.Width, currentItem.CellBounds.Height);
                currentItem.Bounds = new Rect(currentItem.Bounds.X + dx, currentItem.Bounds.Y, currentItem.Bounds.Width, currentItem.Bounds.Height);
            }
        }
    }

    protected override void ShiftItemsConsecutively(IReadOnlyList<VirtualizeListViewItem> items, int start, int exclusiveEnd)
    {
        var count = items.Count;

        if (IsOrientation(ScrollOrientation.Both) || start < 0
            || start >= count || exclusiveEnd <= 0 || exclusiveEnd > count) return;

        var prevIndex = start - 1;
        var prevBounds = prevIndex == -1 ? new() : items[prevIndex].Bounds;

        if (IsOrientation(ScrollOrientation.Vertical))
        {
            for (int i = start; i < exclusiveEnd; i++)
            {
                var item = items[i];

                var dy = prevBounds.Bottom - item.Bounds.Y;
                if (dy == 0d)
                {
                    prevBounds = item.Bounds;
                    continue;
                }

                item.CellBounds = new Rect(item.CellBounds.X, item.CellBounds.Y + dy, item.CellBounds.Width, item.CellBounds.Height);
                item.Bounds = new Rect(item.Bounds.X, item.Bounds.Y + dy, item.Bounds.Width, item.Bounds.Height);

                prevBounds = item.Bounds;
            }
        }
        else
        {
            for (int i = start; i < exclusiveEnd; i++)
            {
                var item = items[i];

                var dx = prevBounds.Right - item.Bounds.X;
                if (dx == 0d)
                {
                    prevBounds = item.Bounds;
                    continue;
                }

                item.CellBounds = new Rect(item.CellBounds.X + dx, item.CellBounds.Y, item.CellBounds.Width, item.CellBounds.Height);
                item.Bounds = new Rect(item.Bounds.X + dx, item.Bounds.Y, item.Bounds.Width, item.Bounds.Height);
            }
        }
    }

    protected override bool AdjustScrollIfNeeded(IReadOnlyList<VirtualizeListViewItem> items, VirtualizeListViewItem prevFirstVisiblItem, Rect prevCellBounds)
    {
        if (IsOrientation(ScrollOrientation.Both) || prevFirstVisiblItem.Position == -1) return false;

        bool needs;

        if (IsOrientation(ScrollOrientation.Vertical))
        {
            var dy = prevFirstVisiblItem.CellBounds.Bottom - prevCellBounds.Bottom;

            needs = dy != 0d;
            if (!needs) return needs;

            Control!.AdjustScroll(0d, dy);
        }
        else
        {
            var dx = prevFirstVisiblItem.CellBounds.Right - prevCellBounds.Right;

            needs = dx != 0d;
            if (!needs) return needs;

            Control!.AdjustScroll(dx, 0d);
        }

        return needs;
    }

    protected override void AdjustScrollForItemBoundsChange(VirtualizeListViewItem item, Rect prevBounds)
    {
        if (IsOrientation(ScrollOrientation.Both)
            || item.Position == -1) return;

        if (IsOrientation(ScrollOrientation.Vertical))
        {
            var bottom = item.Bounds.Bottom;
            var top = item.Bounds.Y + Control!.Padding.Top;
            var dy = bottom - prevBounds.Bottom;

            var scrollY = Control.ScrollY;

            if (dy == 0d || (top < (scrollY + Control.Height) && top > scrollY)) return;

            Control.AdjustScroll(0d, dy);
        }
        else
        {
            var right = item.Bounds.Right;
            var left = item.Bounds.X + Control!.Padding.Left;
            var dx = right - prevBounds.Right;

            var scrollX = Control.ScrollX;

            if (dx == 0d || (left < (scrollX + Control.Width) && left > scrollX)) return;

            Control.AdjustScroll(dx, 0d);
        }
    }

    protected override Thickness GetItemMargin(VirtualizeListViewItem item)
    {
        if (IsOrientation(ScrollOrientation.Both)
            || item.Position <= 0) return base.GetItemMargin(item);

        return IsOrientation(ScrollOrientation.Vertical)
            ? new(0d, ItemSpacing, 0d, 0d)
            : new(ItemSpacing, 0d, 0d, 0d);
    }
}