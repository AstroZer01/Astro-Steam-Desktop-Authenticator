using System;
using Steam_Desktop_Authenticator;
using Xunit;

namespace SteamAuth.PhoneEnrollment.Tests
{
    public sealed class MainFormAspectRatioResizeTests
    {
        private const double Ratio = 920.0 / 660.0;
        private const int MinWidth = 600;
        private const int MinHeight = 430;

        [Theory]
        [InlineData(1)] // WMSZ_LEFT
        [InlineData(2)] // WMSZ_RIGHT
        [InlineData(3)] // WMSZ_TOP
        [InlineData(4)] // WMSZ_TOPLEFT
        [InlineData(5)] // WMSZ_TOPRIGHT
        [InlineData(6)] // WMSZ_BOTTOM
        [InlineData(7)] // WMSZ_BOTTOMLEFT
        [InlineData(8)] // WMSZ_BOTTOMRIGHT
        public void AdjustResizeRectForAspectRatio_PreservesAspectRatioAndClampsMinimum(int edge)
        {
            var growing = new MainForm.ResizeRect { Left = 100, Top = 100, Right = 1300, Bottom = 850 };
            MainForm.AdjustResizeRectForAspectRatio(ref growing, edge, Ratio, MinWidth, MinHeight);

            int grownWidth = growing.Right - growing.Left;
            int grownHeight = growing.Bottom - growing.Top;
            Assert.True(grownWidth >= MinWidth);
            Assert.True(grownHeight >= MinHeight);
            Assert.InRange((double)grownWidth / grownHeight, Ratio - 0.01, Ratio + 0.01);

            var shrinkingTooSmall = new MainForm.ResizeRect { Left = 100, Top = 100, Right = 300, Bottom = 250 };
            MainForm.AdjustResizeRectForAspectRatio(ref shrinkingTooSmall, edge, Ratio, MinWidth, MinHeight);

            int clampedWidth = shrinkingTooSmall.Right - shrinkingTooSmall.Left;
            int clampedHeight = shrinkingTooSmall.Bottom - shrinkingTooSmall.Top;
            Assert.True(clampedWidth >= MinWidth);
            Assert.True(clampedHeight >= MinHeight);
            Assert.InRange((double)clampedWidth / clampedHeight, Ratio - 0.01, Ratio + 0.01);
        }

        [Theory]
        [InlineData(1, false, true, true, false)]  // WMSZ_LEFT: anchors Right, Top
        [InlineData(2, true, false, true, false)]  // WMSZ_RIGHT: anchors Left, Top
        [InlineData(3, true, false, false, true)]  // WMSZ_TOP: anchors Left, Bottom
        [InlineData(4, false, true, false, true)]  // WMSZ_TOPLEFT: anchors Right, Bottom
        [InlineData(5, true, false, false, true)]  // WMSZ_TOPRIGHT: anchors Left, Bottom
        [InlineData(6, true, false, true, false)]  // WMSZ_BOTTOM: anchors Left, Top
        [InlineData(7, false, true, true, false)]  // WMSZ_BOTTOMLEFT: anchors Right, Top
        [InlineData(8, true, false, true, false)]  // WMSZ_BOTTOMRIGHT: anchors Left, Top
        public void AdjustResizeRectForAspectRatio_AnchorsOppositeEdgesAcrossAllDirections(
            int edge,
            bool anchorLeft,
            bool anchorRight,
            bool anchorTop,
            bool anchorBottom)
        {
            var rect = new MainForm.ResizeRect { Left = 50, Top = 80, Right = 1200, Bottom = 900 };
            MainForm.AdjustResizeRectForAspectRatio(ref rect, edge, Ratio, MinWidth, MinHeight);

            if (anchorLeft) Assert.Equal(50, rect.Left);
            if (anchorRight) Assert.Equal(1200, rect.Right);
            if (anchorTop) Assert.Equal(80, rect.Top);
            if (anchorBottom) Assert.Equal(900, rect.Bottom);
        }

        [Fact]
        public void AdjustResizeRectForAspectRatio_HandlesNonPositiveRatioAndInvertedRectSafely()
        {
            var unchanged = new MainForm.ResizeRect { Left = 10, Top = 20, Right = 500, Bottom = 400 };
            MainForm.AdjustResizeRectForAspectRatio(ref unchanged, 8, 0.0, MinWidth, MinHeight);
            Assert.Equal(10, unchanged.Left);
            Assert.Equal(20, unchanged.Top);
            Assert.Equal(500, unchanged.Right);
            Assert.Equal(400, unchanged.Bottom);

            var inverted = new MainForm.ResizeRect { Left = 600, Top = 500, Right = 200, Bottom = 100 };
            MainForm.AdjustResizeRectForAspectRatio(ref inverted, 8, Ratio, MinWidth, MinHeight);
            Assert.True(inverted.Right - inverted.Left >= MinWidth);
            Assert.True(inverted.Bottom - inverted.Top >= MinHeight);
        }
    }
}
