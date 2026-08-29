using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace MainAPP.Controls;

public sealed class VirtualizingWrapPanel : VirtualizingPanel, IScrollInfo
{
    private const int CacheRows = 1;
    private const double InitialItemHeight = 120;
    private static readonly DependencyProperty RealizedIndexProperty = DependencyProperty.RegisterAttached(
        "RealizedIndex",
        typeof(int),
        typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(-1));
    private double _itemHeight = InitialItemHeight;
    private double _itemSlotWidth = 1;
    private int _itemsPerRow = 1;
    private Size _extent;
    private Size _viewport;
    private Vector _offset;
    private ScrollViewer? _scrollOwner;

    public static readonly DependencyProperty ItemWidthProperty = DependencyProperty.Register(
        nameof(ItemWidth),
        typeof(double),
        typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double ItemWidth
    {
        get => (double)GetValue(ItemWidthProperty);
        set => SetValue(ItemWidthProperty, value);
    }

    /// <summary>
    /// 自适应列宽下限（&gt;0 时启用）。列数 = floor(面板宽 / MinItemWidth)，每列均分剩余宽度（等同 CSS minmax + 1fr）。
    /// 为 0 时沿用固定 <see cref="ItemWidth"/> 槽位。
    /// </summary>
    public static readonly DependencyProperty MinItemWidthProperty = DependencyProperty.Register(
        nameof(MinItemWidth),
        typeof(double),
        typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double MinItemWidth
    {
        get => (double)GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    private void UpdateColumnLayout(double panelWidth)
    {
        if (MinItemWidth > 0)
        {
            var width = panelWidth > 0 ? panelWidth : MinItemWidth;
            _itemsPerRow = Math.Max(1, (int)Math.Floor(width / MinItemWidth));
            _itemSlotWidth = width / _itemsPerRow;
            return;
        }

        _itemSlotWidth = Math.Max(1, ItemWidth);
        var basis = panelWidth > 0 ? panelWidth : _itemSlotWidth;
        _itemsPerRow = Math.Max(1, (int)Math.Floor(basis / _itemSlotWidth));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var itemCount = ItemsControl.GetItemsOwner(this)?.Items.Count ?? 0;
        var panelWidth = double.IsInfinity(availableSize.Width) || availableSize.Width <= 0
            ? (MinItemWidth > 0 ? MinItemWidth : ItemWidth)
            : availableSize.Width;
        UpdateColumnLayout(panelWidth);
        var itemWidth = _itemSlotWidth;

        if (itemCount == 0)
        {
            RemoveAllChildren();
            UpdateScrollInfo(new Size(panelWidth, 0), new Size(panelWidth, Math.Max(0, availableSize.Height)));
            return availableSize;
        }

        var rowCount = (itemCount + _itemsPerRow - 1) / _itemsPerRow;
        var viewportHeight = double.IsInfinity(availableSize.Height)
            ? rowCount * _itemHeight
            : Math.Max(0, availableSize.Height);
        UpdateScrollInfo(
            new Size(panelWidth, rowCount * _itemHeight),
            new Size(panelWidth, viewportHeight));

        var firstRow = Math.Max(0, (int)Math.Floor(_offset.Y / _itemHeight) - CacheRows);
        var lastRow = Math.Min(
            rowCount - 1,
            (int)Math.Ceiling((_offset.Y + viewportHeight) / _itemHeight) + CacheRows);
        var firstIndex = firstRow * _itemsPerRow;
        var lastIndex = Math.Min(itemCount - 1, ((lastRow + 1) * _itemsPerRow) - 1);

        RemoveChildrenOutsideRange(firstIndex, lastIndex);
        GenerateChildren(firstIndex, lastIndex, itemWidth);

        var measuredHeight = InternalChildren
            .Cast<UIElement>()
            .Select(child => child.DesiredSize.Height)
            .Where(height => height > 0)
            .DefaultIfEmpty(_itemHeight)
            .Max();
        if (Math.Abs(measuredHeight - _itemHeight) > 0.5)
        {
            _itemHeight = measuredHeight;
            UpdateScrollInfo(
                new Size(panelWidth, rowCount * _itemHeight),
                new Size(panelWidth, viewportHeight));
            InvalidateMeasure();
        }

        return availableSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (UIElement child in InternalChildren)
        {
            var index = (int)child.GetValue(RealizedIndexProperty);
            if (index < 0) continue;

            var row = index / _itemsPerRow;
            var column = index % _itemsPerRow;
            child.Arrange(new Rect(
                column * _itemSlotWidth,
                row * _itemHeight - _offset.Y,
                _itemSlotWidth,
                _itemHeight));
        }

        return finalSize;
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        base.OnItemsChanged(sender, args);
        InvalidateMeasure();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        InvalidateMeasure();
    }

    private void GenerateChildren(int firstIndex, int lastIndex, double itemWidth)
    {
        if (firstIndex > lastIndex) return;

        var generator = ItemContainerGenerator;
        var childIndex = 0;
        using (generator.StartAt(generator.GeneratorPositionFromIndex(firstIndex), GeneratorDirection.Forward, true))
        {
            for (var index = firstIndex; index <= lastIndex; index++)
            {
                var child = (UIElement)generator.GenerateNext(out var isNewlyRealized);
                if (isNewlyRealized)
                    InsertInternalChild(childIndex, child);

                child.SetValue(RealizedIndexProperty, index);
                generator.PrepareItemContainer(child);
                child.Measure(new Size(itemWidth, double.PositiveInfinity));
                childIndex++;
            }
        }
    }

    private void RemoveChildrenOutsideRange(int firstIndex, int lastIndex)
    {
        var generator = ItemContainerGenerator;
        for (var childIndex = InternalChildren.Count - 1; childIndex >= 0; childIndex--)
        {
            var child = InternalChildren[childIndex];
            var itemIndex = (int)child.GetValue(RealizedIndexProperty);
            if (itemIndex >= firstIndex && itemIndex <= lastIndex) continue;

            if (itemIndex >= 0)
                generator.Remove(generator.GeneratorPositionFromIndex(itemIndex), 1);
            RemoveInternalChildRange(childIndex, 1);
        }
    }

    private void RemoveAllChildren()
    {
        ItemContainerGenerator.RemoveAll();
        for (var index = InternalChildren.Count - 1; index >= 0; index--)
            RemoveInternalChildRange(index, 1);
    }

    private void UpdateScrollInfo(Size extent, Size viewport)
    {
        _extent = extent;
        _viewport = viewport;
        SetVerticalOffset(_offset.Y);
        _scrollOwner?.InvalidateScrollInfo();
    }

    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; } = true;
    public double ExtentWidth => _extent.Width;
    public double ExtentHeight => _extent.Height;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => _offset.X;
    public double VerticalOffset => _offset.Y;

    public ScrollViewer? ScrollOwner
    {
        get => _scrollOwner;
        set => _scrollOwner = value;
    }

    public void LineDown() => SetVerticalOffset(_offset.Y + _itemHeight);
    public void LineLeft() => SetHorizontalOffset(_offset.X - _itemSlotWidth);
    public void LineRight() => SetHorizontalOffset(_offset.X + _itemSlotWidth);
    public void LineUp() => SetVerticalOffset(_offset.Y - _itemHeight);
    public void MouseWheelDown() => SetVerticalOffset(_offset.Y + (3 * _itemHeight));
    public void MouseWheelLeft() => SetHorizontalOffset(_offset.X - (3 * _itemSlotWidth));
    public void MouseWheelRight() => SetHorizontalOffset(_offset.X + (3 * _itemSlotWidth));
    public void MouseWheelUp() => SetVerticalOffset(_offset.Y - (3 * _itemHeight));
    public void PageDown() => SetVerticalOffset(_offset.Y + _viewport.Height);
    public void PageLeft() => SetHorizontalOffset(_offset.X - _viewport.Width);
    public void PageRight() => SetHorizontalOffset(_offset.X + _viewport.Width);
    public void PageUp() => SetVerticalOffset(_offset.Y - _viewport.Height);

    public void SetHorizontalOffset(double offset)
    {
        _offset.X = 0;
        if (Math.Abs(offset) > 0) _scrollOwner?.InvalidateScrollInfo();
    }

    public void SetVerticalOffset(double offset)
    {
        var maxOffset = Math.Max(0, _extent.Height - _viewport.Height);
        var coercedOffset = Math.Clamp(offset, 0, maxOffset);
        if (Math.Abs(coercedOffset - _offset.Y) < 0.1) return;

        _offset.Y = coercedOffset;
        InvalidateMeasure();
        _scrollOwner?.InvalidateScrollInfo();
    }

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        var current = (DependencyObject)visual;
        var index = -1;
        while (current is not null && current != this)
        {
            if (current is DependencyObject container)
                index = (int)container.GetValue(RealizedIndexProperty);
            if (index >= 0) break;
            current = VisualTreeHelper.GetParent(current);
        }

        if (index < 0) return rectangle;

        var row = index / _itemsPerRow;
        var top = row * _itemHeight;
        var bottom = top + _itemHeight;
        if (top < _offset.Y)
            SetVerticalOffset(top);
        else if (bottom > _offset.Y + _viewport.Height)
            SetVerticalOffset(bottom - _viewport.Height);

        return rectangle;
    }
}