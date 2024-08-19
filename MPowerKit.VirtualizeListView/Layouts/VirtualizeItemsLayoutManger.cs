﻿using System.ComponentModel;
using System.Runtime.InteropServices;

using Microsoft.Maui.Controls.Internals;
using Microsoft.Maui.Layouts;

using static MPowerKit.VirtualizeListView.DataAdapter;

namespace MPowerKit.VirtualizeListView;

public abstract class VirtualizeItemsLayoutManger : Layout, ILayoutManager, IDisposable
{
    protected const double AutoSize = -1d;
    protected const double CachedItemsCoords = -1000000d;
    protected static double EstimatedSize { get; set; } = 200d;

    protected ScrollEventArgs PrevScroll { get; set; } = new(0d, 0d, 0d, 0d);
    protected Point PrevScrollBeforeSizeChange { get; set; }
    protected Size PrevContentSize { get; set; }
    protected Size PrevAvailableSpace { get; set; }

    public VirtualizeListView? Control { get; protected set; }
    public int CachePoolSize { get; set; }
    public bool IsDisposed { get; protected set; }

    protected List<VirtualizeListViewItem> LaidOutItems { get; } = [];

    public IReadOnlyList<VirtualizeListViewItem> ReadOnlyLaidOutItems => LaidOutItems;

    protected List<(DataTemplate Template, CellHolder Cell)> CachedCells { get; } = [];

    public List<(AdapterItem Data, int Position)> VisibleItems => LaidOutItems
        .FindAll(i => i.IsOnScreen && i.IsAttached && i.Cell?.Children[0] is VirtualizeListViewCell)
        .Select(i => (i.AdapterItem, i.Position))
        .ToList();

    protected virtual Size AvailableSpace => GetAvailableSpace();

    protected override ILayoutManager CreateLayoutManager()
    {
        return this;
    }

    protected override void OnParentChanging(ParentChangingEventArgs args)
    {
        base.OnParentChanging(args);

        UnsubscribeFromEvents();

        Control = null;
    }

    protected override void OnParentChanged()
    {
        base.OnParentChanged();

        if (this.Parent is null) return;

        if (this.Parent is not VirtualizeListView listView)
        {
            throw new InvalidOperationException("ItemsLayoutManager can be used only within VirtualizeListView");
        }

        Control = listView;

        SubscribeToEvents();
    }

    protected virtual void UnsubscribeFromEvents()
    {
        if (Control is null) return;

        OnAdapterReset();

        Control.PropertyChanging -= Control_PropertyChanging;
        Control.PropertyChanged -= Control_PropertyChanged;
        Control.SizeChanged -= Control_SizeChanged;
        Control.Scrolled -= Control_Scrolled;
    }

    protected virtual void SubscribeToEvents()
    {
        if (Control is null) return;

        Control.PropertyChanging += Control_PropertyChanging;
        Control.PropertyChanged += Control_PropertyChanged;
        Control.SizeChanged += Control_SizeChanged;
        Control.Scrolled += Control_Scrolled;

        OnAdapterSet();
    }

    protected virtual bool DoesScrollHaveSize()
    {
        return Control is not null && !double.IsNaN(Control.Width) && !double.IsNaN(Control.Height) && Control.Width >= 0d && Control.Height >= 0d;
    }

    protected virtual void OnAdapterSet()
    {
        if (Control?.Adapter is null) return;

        Control.Adapter.DataSetChanged += AdapterDataSetChanged;
        Control.Adapter.ItemMoved += AdapterItemMoved;
        Control.Adapter.ItemRangeChanged += AdapterItemRangeChanged;
        Control.Adapter.ItemRangeInserted += AdapterItemRangeInserted;
        Control.Adapter.ItemRangeRemoved += AdapterItemRangeRemoved;

        InvalidateLayout();
    }

    protected virtual void OnAdapterReset()
    {
        if (Control?.Adapter is null) return;

        Control.Adapter.DataSetChanged -= AdapterDataSetChanged;
        Control.Adapter.ItemMoved -= AdapterItemMoved;
        Control.Adapter.ItemRangeChanged -= AdapterItemRangeChanged;
        Control.Adapter.ItemRangeInserted -= AdapterItemRangeInserted;
        Control.Adapter.ItemRangeRemoved -= AdapterItemRangeRemoved;
    }

    protected virtual void Control_Scrolled(object? sender, ScrolledEventArgs e)
    {
        var newScroll = e - PrevScroll;
        UpdateItemsLayout(0, true);
        PrevScroll = newScroll;
    }

    protected virtual void Control_PropertyChanging(object? sender, Microsoft.Maui.Controls.PropertyChangingEventArgs e)
    {
        if (e.PropertyName == VirtualizeListView.AdapterProperty.PropertyName)
        {
            OnAdapterReset();
        }
    }

    protected virtual void Control_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == VirtualizeListView.AdapterProperty.PropertyName)
        {
            OnAdapterSet();
        }
        //else if (e.PropertyName == VirtualizeListView.ContentSizeProperty.PropertyName)
        //{
        //    AdjustScrollPosition();
        //}
        else if (e.PropertyName == VirtualizeListView.PaddingProperty.PropertyName)
        {
            Control_SizeChanged(Control, EventArgs.Empty);
        }
    }

    protected virtual void Control_SizeChanged(object? sender, EventArgs e)
    {
        var newSpace = GetAvailableSpace();

        if (newSpace == PrevAvailableSpace) return;

        PrevAvailableSpace = newSpace;

        if (LaidOutItems.Count == 0)
        {
            InvalidateLayout();
            return;
        }
        else
        {
            RelayoutItems();
        }
    }

    /// <summary>
    /// Use only if there was no any items before, or itemtemplate has changed.
    /// Clears all items and lays out new ones.
    /// </summary>
    public virtual void InvalidateLayout()
    {
        LaidOutItems.Clear();
        CachedCells.Clear();
        this.Clear();
        (this as IView).InvalidateMeasure();

        if (!DoesScrollHaveSize()) return;

        if (Control?.Adapter?.ItemsCount is null or 0) return;

        var count = Control.Adapter.ItemsCount;

        var availableSpace = AvailableSpace;

        for (int i = 0; i < count; i++)
        {
            var item = CreateItemForPosition(i);

            LaidOutItems.Add(item);

            ShiftItemsChunk(LaidOutItems, i, LaidOutItems.Count);

            if (!item.IsOnScreen) continue;

            ReuseCell(item, true, availableSpace);
        }

        CreateCachePool(CachePoolSize);

        DrawAndTriggerResize();
    }

    protected virtual void CreateCachePool(int poolSize)
    {
        if (Control?.Adapter is null || poolSize <= 0) return;

        var pool = Control.Adapter.CreateCellsPool(4);

        for (int i = 0; i < pool.Count; i++)
        {
            var (cell, template) = pool[i];
            this.Add(cell);

            var item = CreateDummyItem(template, cell);

            CacheCell(item);
        }
    }

    protected virtual void RelayoutItems()
    {
        var count = LaidOutItems.Count;
        if (!DoesScrollHaveSize() || count == 0) return;

        var attachedItems = LaidOutItems.FindAll(i => i.IsAttached);

        if (attachedItems.Count == 0)
        {
            UpdateItemsLayout(0, false);
            return;
        }

        foreach (var item in attachedItems)
        {
            ArrangeItem(LaidOutItems, item, AvailableSpace);
        }

        UpdateItemsLayout(attachedItems[^1].Position + 1, false);
    }

    protected virtual void AdapterDataSetChanged(object? sender, EventArgs e)
    {
        if (Control!.Adapter.ItemsCount == 0 && LaidOutItems.Count == 0)
        {
            return;
        }
        else if (LaidOutItems.Count == 0 && CachedCells.Count == 0)
        {
            InvalidateLayout();
        }
        else if (Control!.Adapter.ItemsCount == 0)
        {
            AdapterItemRangeRemoved(this, (0, LaidOutItems.Count));
        }
        else if (LaidOutItems.Count > 0)
        {
            AdapterItemRangeChanged(this, (0, LaidOutItems.Count, Control.Adapter.ItemsCount));
            if (Control.ScrollX == 0d && Control.ScrollY == 0d) return;
            Control.ScrollToAsync(0, 0, false);
        }
        else if (LaidOutItems.Count == 0)
        {
            AdapterItemRangeInserted(this, (0, Control.Adapter.ItemsCount));
        }
    }

    protected virtual void AdapterItemRangeInserted(object? sender, (int StartingIndex, int TotalCount) e)
    {
        if (!DoesScrollHaveSize()) return;

        var count = LaidOutItems.Count;
        var startingIndex = e.StartingIndex;

        if (Control!.Adapter.ItemsCount == 0 || e.StartingIndex > count)
        {
            throw new ArgumentException("Invalid range");
        }

        if (count == 0 && CachedCells.Count == 0)
        {
            InvalidateLayout();
            return;
        }

        var finishIndex = startingIndex + e.TotalCount;

        var itemsToRearrange = LaidOutItems.FindAll(i => i.IsOnScreen && i.IsAttached);
        var firstVisibleItem = itemsToRearrange.FirstOrDefault();
        var prevVisibleCellBounds = firstVisibleItem?.Bounds ?? new();

        for (int index = startingIndex; index < finishIndex; index++)
        {
            var item = CreateItemForPosition(index);

            LaidOutItems.Insert(index, item);
        }

        RepositionItemsFromIndex(LaidOutItems, finishIndex);

        ShiftItemsConsecutively(LaidOutItems, startingIndex, finishIndex);
        ShiftItemsChunk(LaidOutItems, finishIndex, LaidOutItems.Count);

        UpdateItemsLayout(startingIndex, false);

        // if we inserted items defore the first visible item
        // then we need to adjust the scroll position
        if (firstVisibleItem is not null && firstVisibleItem.Position >= finishIndex)
        {
            AdjustScrollIfNeeded(LaidOutItems, firstVisibleItem, prevVisibleCellBounds);
        }
    }

    protected virtual void AdapterItemRangeRemoved(object? sender, (int StartingIndex, int TotalCount) e)
    {
        if (!DoesScrollHaveSize()) return;

        var count = LaidOutItems.Count;
        var startingIndex = e.StartingIndex;
        var finishIndex = startingIndex + e.TotalCount;

        if (count == 0 || finishIndex > count || startingIndex < 0)
        {
            throw new ArgumentException("Invalid range");
        }

        var itemsToRemove = LaidOutItems[startingIndex..finishIndex];
        LaidOutItems.RemoveRange(startingIndex, e.TotalCount);

        for (int i = 0; i < e.TotalCount; i++)
        {
            DetachCell(itemsToRemove[i]);
        }

        if (LaidOutItems.Count == 0)
        {
            (this as IView).InvalidateMeasure();
            return;
        }

        RepositionItemsFromIndex(LaidOutItems, startingIndex);

        var itemsToRearrange = LaidOutItems.FindAll(i => i.IsOnScreen && i.IsAttached);
        var firstVisibleItem = itemsToRearrange.FirstOrDefault();
        var prevVisibleCellBounds = firstVisibleItem?.Bounds ?? new();

        ShiftItemsChunk(LaidOutItems, startingIndex, LaidOutItems.Count);

        // if we removed items from the beginning
        if (startingIndex == 0)
        {
            // if we are at the top we dont need to adjust the scroll position
            if (Control!.ScrollX == 0d && Control.ScrollY == 0d)
            {
                UpdateItemsLayout(startingIndex, false);
                return;
            }

            // otherwise we need to adjust the scroll position
            Control.ScrollToAsync(0d, 0d, false);
            return;
        }

        // if we removed all visible items
        // then we need to adjust the scroll position
        if (firstVisibleItem is null)
        {
            var prevItem = LaidOutItems[startingIndex - 1];

            UpdateItemsLayout(startingIndex, false);
            AdjustScrollIfNeeded(LaidOutItems, prevItem, new(Control!.ScrollX - Control.Padding.Left, Control.ScrollY - Control.Padding.Top, 0, 0));
            return;
        }

        // if we removed items after the first visible item
        // so we dont need to adjust the scroll position
        if (firstVisibleItem.Position < startingIndex)
        {
            UpdateItemsLayout(startingIndex, false);
            return;
        }

        // if we removed items before the first visible item
        // then we also need to adjust the scroll position
        if (firstVisibleItem.Position >= startingIndex)
        {
            AdjustScrollIfNeeded(LaidOutItems, firstVisibleItem, prevVisibleCellBounds);
        }
    }

    protected virtual void AdapterItemRangeChanged(object? sender, (int StartingIndex, int OldCount, int NewCount) e)
    {
        if (!DoesScrollHaveSize()) return;

        var count = LaidOutItems.Count;
        var start = e.StartingIndex;
        var oldCount = e.OldCount;
        var newCount = e.NewCount;
        var oldEnd = start + e.OldCount;
        var newEnd = start + e.NewCount;
        var adapterItemsCount = Control!.Adapter.ItemsCount;

        if (count == 0 || adapterItemsCount == 0 || start < 0 || oldEnd > count || newEnd > adapterItemsCount)
        {
            throw new ArgumentException("Invalid range");
        }

        var itemsToRemove = CollectionsMarshal.AsSpan(LaidOutItems[start..oldEnd]);
        LaidOutItems.RemoveRange(start, oldCount);

        for (int i = 0; i < oldCount; i++)
        {
            //remove
            var itemToRemove = itemsToRemove[i];

            DetachCell(itemToRemove);
        }

        var itemsToRearrange = LaidOutItems.FindAll(i => i.IsOnScreen && i.IsAttached);
        var firstVisibleItem = itemsToRearrange.FirstOrDefault();
        var prevVisibleCellBounds = firstVisibleItem?.Bounds ?? new();

        for (int index = start; index < newEnd; index++)
        {
            var item = CreateItemForPosition(index);

            LaidOutItems.Insert(index, item);
        }

        RepositionItemsFromIndex(LaidOutItems, newEnd);

        ShiftItemsConsecutively(LaidOutItems, start, newEnd);
        ShiftItemsChunk(LaidOutItems, newEnd, LaidOutItems.Count);

        // if we replaced items from the beginning
        if (start == 0)
        {
            // if we are at the top we dont need to adjust the scroll position
            if (Control!.ScrollX == 0d && Control.ScrollY == 0d)
            {
                UpdateItemsLayout(start, false);
                return;
            }

            // if we replaced items before the first visible item
            // then we also need to adjust the scroll position
            if (firstVisibleItem is not null && firstVisibleItem.Position >= start)
            {
                AdjustScrollIfNeeded(LaidOutItems, firstVisibleItem, prevVisibleCellBounds);
                return;
            }

            // otherwise we need to adjust the scroll position
            Control.ScrollToAsync(0d, 0d, false);
            return;
        }

        // if there is no any visible item 
        // or we replaced items after the first visible item
        // so we dont need to adjust the scroll position
        if (firstVisibleItem is null || firstVisibleItem.Position < start)
        {
            UpdateItemsLayout(start, false);
            return;
        }

        // if we replaced items before the first visible item
        // then we also need to adjust the scroll position
        if (firstVisibleItem.Position >= start)
        {
            AdjustScrollIfNeeded(LaidOutItems, firstVisibleItem, prevVisibleCellBounds);
        }
    }

    protected virtual void AdapterItemMoved(object? sender, (int OldIndex, int NewIndex) e)
    {
        if (!DoesScrollHaveSize()) return;

        var count = LaidOutItems.Count;
        var newIndex = e.NewIndex;
        var oldIndex = e.OldIndex;

        if (Control?.Adapter?.ItemsCount is null or 0
            || count == 0 || newIndex < 0 || newIndex >= count || oldIndex < 0
            || oldIndex >= count || oldIndex == newIndex)
        {
            throw new ArgumentException("Invalid range");
        }

        var start = Math.Min(oldIndex, newIndex);
        var end = Math.Max(oldIndex, newIndex);

        var itemsToRearrange = LaidOutItems.FindAll(i => i.IsOnScreen && i.IsAttached);
        var firstVisibleItem = itemsToRearrange.First();
        var lastVisibleItem = itemsToRearrange.Last();
        var prevVisibleCellBounds = firstVisibleItem.Bounds;

        var itemToMove = LaidOutItems[oldIndex];

        LaidOutItems.RemoveAt(oldIndex);
        LaidOutItems.Insert(newIndex, itemToMove);

        RepositionItemsFromIndex(LaidOutItems, start);

        if ((start < firstVisibleItem.Position && end < firstVisibleItem.Position)
            || (start > lastVisibleItem.Position && end > lastVisibleItem.Position))
        {
            ShiftItemsConsecutively(LaidOutItems, start, end + 1);
            return;
        }

        if (firstVisibleItem == itemToMove)
        {
            firstVisibleItem = itemsToRearrange.FirstOrDefault(i => i != itemToMove);
            prevVisibleCellBounds = firstVisibleItem?.Bounds ?? new();
            itemsToRearrange.Remove(itemToMove);
        }
        else if (lastVisibleItem == itemToMove)
        {
            lastVisibleItem = itemsToRearrange.LastOrDefault(i => i != itemToMove);
            itemsToRearrange.Remove(itemToMove);
        }

        ShiftItemsChunk(LaidOutItems, start, itemToMove.Position);
        if (itemToMove.Cell is not null)
        {
            ArrangeItem(LaidOutItems, itemToMove, AvailableSpace);
        }
        ShiftItemsConsecutively(LaidOutItems, itemToMove.Position, end + 1);

        if (itemsToRearrange.Count != 0)
        {
            foreach (var item in itemsToRearrange)
            {
                ArrangeItem(LaidOutItems, item, AvailableSpace);
            }

            ShiftItemsChunk(LaidOutItems, itemsToRearrange[^1].Position + 1, count);
        }

        if (firstVisibleItem is not null && AdjustScrollIfNeeded(LaidOutItems, firstVisibleItem, prevVisibleCellBounds)
            && (Control.ScrollX != 0d || Control.ScrollY != 0d)) return;

        UpdateItemsLayout(start, false);
    }

    public virtual void OnItemSizeChanged(VirtualizeListViewItem item)
    {
        ArrangeItem(LaidOutItems, item, AvailableSpace);
        ShiftItemsChunk(LaidOutItems, item.Position + 1, LaidOutItems.Count);
        UpdateItemsLayout(item.Position + 1, false);
    }

    protected virtual void UpdateItemsLayout(int fromPosition, bool shouldAdjustScroll)
    {
        var count = LaidOutItems.Count;

        if (count == 0) return;

        var control = Control!;

        var availableSpace = AvailableSpace;

        var spanList = CollectionsMarshal.AsSpan(LaidOutItems);

        for (int i = fromPosition; i < count; i++)
        {
            var item = spanList[i];

            if (!item.IsOnScreen) DetachCell(item);
        }

        bool reused = false;

        for (int i = fromPosition; i < count; i++)
        {
            var item = spanList[i];

            if (!item.IsOnScreen || item.IsAttached) continue;

            var prevBounds = item.Bounds;

            ReuseCell(item, true, availableSpace);

            reused = true;

            if (item.Bounds == prevBounds) continue;

            ShiftItemsChunk(LaidOutItems, item.Position + 1, count);

            if (!shouldAdjustScroll) continue;

            AdjustScrollForItemBoundsChange(LaidOutItems, item, prevBounds);
        }

        var sizeChanged = DrawAndTriggerResize();

#if !MACIOS
        if (reused && !sizeChanged) (this as IView).InvalidateMeasure();
#endif
    }

    protected virtual void DetachCell(VirtualizeListViewItem item)
    {
        if (!item.IsAttached || item.Cell is null) return;

        Control!.Adapter.OnCellRecycled(item.Cell!, item.AdapterItem, item.Position);

        CacheCell(item);
    }

    protected virtual void CacheCell(VirtualizeListViewItem item)
    {
        if (item.Cell is null) return;

        var cell = item.Cell;

        cell.TranslationX = CachedItemsCoords;
        cell.TranslationY = CachedItemsCoords;

        CachedCells.Add((item.Template, cell));
        item.Cell = null;
    }

    protected virtual void ReuseCell(VirtualizeListViewItem item, bool createNewIfNoCached, Size availableSpace)
    {
        if (item.Cell is not null)
        {
            ArrangeItem(LaidOutItems, item, availableSpace);
            return;
        }

        var freeCell = CachedCells.Find(i => (i.Template as IDataTemplateController).Id == (item.Template as IDataTemplateController).Id);
        if (freeCell != default)
        {
            CachedCells.Remove(freeCell);

            var cell = freeCell.Cell;

            item.Cell = cell;
#if ANDROID
            cell.TranslationX = 0d;
            cell.TranslationY = 0d;
#endif
        }
        else
        {
            var freeItem = LaidOutItems.Find(i =>
                (i.Template as IDataTemplateController).Id == (item.Template as IDataTemplateController).Id
                && !i.IsAttached && !i.IsOnScreen && i.Cell is not null);
            if (freeItem is not null)
            {
                var cell = freeItem.Cell;
                freeItem.Cell = null;
                item.Cell = cell;
            }
            else if (createNewIfNoCached)
            {
                item.Cell = Control!.Adapter.OnCreateCell(item.Template, item.Position);
                this.Add(item.Cell);
            }
        }

        Control!.Adapter.OnBindCell(item.Cell!, item.AdapterItem, item.Position);

        ArrangeItem(LaidOutItems, item, availableSpace);
    }

    protected virtual bool DrawAndTriggerResize()
    {
#if !ANDROID
        // on Android we must arrange item in real bounds, not using translation
        // issue #5
        foreach (var item in LaidOutItems.FindAll(i => i.Cell is not null))
        {
            DrawItem(LaidOutItems, item);
        }
#endif
        return TriggerResizeLayout();
    }

    protected virtual void DrawItem(IReadOnlyList<VirtualizeListViewItem> items, VirtualizeListViewItem item)
    {
        if (items.Count == 0 || item.Position < 0 || item.Cell is null) return;

        var view = item.Cell;

#if !ANDROID
        // on Android we must arrange item in real bounds, not using translation
        // issue #5
        if (view.TranslationX != item.Bounds.X ||
            view.TranslationY != item.Bounds.Y)
        {
            view.TranslationX = item.Bounds.X;
            view.TranslationY = item.Bounds.Y;
        }
#endif
    }

    protected virtual bool TriggerResizeLayout()
    {
        Size desiredSize = new();
        if (IsOrientation(ScrollOrientation.Vertical))
        {
            desiredSize = GetDesiredLayoutSize(AvailableSpace.Width, double.PositiveInfinity);
        }

        if (PrevContentSize == desiredSize) return false;

        PrevContentSize = desiredSize;

        (this as IView).InvalidateMeasure();

        return true;
    }

    protected virtual Size GetAvailableSpace()
    {
        return new Size(Control!.Width - Control.Padding.HorizontalThickness, Control.Height - Control.Padding.VerticalThickness);
    }

    protected virtual VirtualizeListViewItem CreateDummyItem(DataTemplate template, CellHolder cell)
    {
        var item = new VirtualizeListViewItem(this)
        {
            Template = template,
            Cell = cell,
            Position = 0
        };

        item.Size = GetEstimatedItemSize(item, AvailableSpace);

        return item;
    }

    protected virtual bool IsOrientation(ScrollOrientation orientation)
    {
        return Control!.Orientation == orientation
            || (Control.Orientation == ScrollOrientation.Neither && Control.PrevScrollOrientation == orientation);
    }

    protected virtual Size GetDesiredLayoutSize(double widthConstraint, double heightConstraint)
    {
        if (IsOrientation(ScrollOrientation.Both) || LaidOutItems.Count == 0) return new();

        return IsOrientation(ScrollOrientation.Vertical)
            ? new(widthConstraint, LaidOutItems[^1].RightBottomWithMargin.Y)
            : new(LaidOutItems[^1].RightBottomWithMargin.X, heightConstraint);
    }

    protected abstract void RepositionItemsFromIndex(IReadOnlyList<VirtualizeListViewItem> items, int index);
    protected abstract VirtualizeListViewItem CreateItemForPosition(int position);
    protected abstract Thickness GetItemMargin(IReadOnlyList<VirtualizeListViewItem> items, VirtualizeListViewItem item);
    protected abstract Size GetEstimatedItemSize(VirtualizeListViewItem item, Size availableSize);
    protected abstract Size MeasureItem(IReadOnlyList<VirtualizeListViewItem> items, VirtualizeListViewItem item, Size availableSpace);
    protected abstract void ArrangeItem(IReadOnlyList<VirtualizeListViewItem> items, VirtualizeListViewItem item, Size availableSpace);
    protected abstract void ShiftItemsChunk(IReadOnlyList<VirtualizeListViewItem> items, int start, int exclusiveEnd);
    protected abstract void ShiftItemsConsecutively(IReadOnlyList<VirtualizeListViewItem> items, int start, int exclusiveEnd);
    protected abstract void AdjustScrollForItemBoundsChange(IReadOnlyList<VirtualizeListViewItem> items, VirtualizeListViewItem item, Rect prevBoundsOfItem);
    protected abstract bool AdjustScrollIfNeeded(IReadOnlyList<VirtualizeListViewItem> items, VirtualizeListViewItem item, Rect prevBoundsOfItem);

    #region ILayoutManager
    public virtual Size Measure(double widthConstraint, double heightConstraint)
    {
        var items = CollectionsMarshal.AsSpan((this as IBindableLayout).Children as List<IView>);
        var length = items.Length;

        var availableSpace = AvailableSpace;

        for (int n = 0; n < length; n++)
        {
            var child = items[n];
            var view = (child as CellHolder)!;

            if ((view.IsCached || !view.Item!.IsAttached)
#if MACIOS
                // on Mac and iOS we must to do initial measure of not measured items
                && view.WasMeasured
#endif
                ) continue;

#if MACIOS
            if (!view.WasMeasured && view.Item is null)
            {
                child.Measure(double.PositiveInfinity, double.PositiveInfinity);
            }
            else
#endif
            // this triggers item size change when needed
            MeasureItem(LaidOutItems, view.Item!, availableSpace);
        }

        var desiredSize = GetDesiredLayoutSize(widthConstraint, heightConstraint);

        return desiredSize;
    }

    public virtual Size ArrangeChildren(Rect bounds)
    {
        var padding = Padding;

        var availableWidth = bounds.Width - padding.HorizontalThickness;
        var availableHeight = bounds.Height - padding.VerticalThickness;

        var items = CollectionsMarshal.AsSpan((this as IBindableLayout).Children as List<IView>);
        var length = items.Length;

        for (int n = 0; n < length; n++)
        {
            var child = items[n];
            var view = (child as CellHolder)!;

            if ((view.IsCached || !view.Item!.IsAttached)
#if MACIOS
                // on Mac and iOS we must to do initial arrange of not arranged items
                && view.WasArranged
#endif
                ) continue;

            Point loc =
#if ANDROID
                // on Android we must arrange item in real bounds, not using translation
                // issue #5
                view.Item.LeftTop;
#else
                new(0d, 0d);
#endif

            Size size = view.Item?.Size ?? new(view.DesiredSize.Width, view.DesiredSize.Height);

            Rect newBounds = new(loc, size);

#if MACIOS
            // on other platforms we need to arrange items anyway
            if (view.Bounds == newBounds) continue;
#endif

            child.Arrange(newBounds);
        }

        return new(availableWidth, availableHeight);
    }
    #endregion

    #region IDisposable
    protected virtual void Dispose(bool disposing)
    {
        if (this.IsDisposed) return;

        UnsubscribeFromEvents();

        IsDisposed = true;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    ~VirtualizeItemsLayoutManger()
    {
        Dispose(false);
    }
    #endregion
}