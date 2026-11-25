using System;
using Steam_Desktop_Authenticator;
using Xunit;

namespace SteamAuth.PhoneEnrollment.Tests
{
    public sealed class MainFormAspectRatioResizeTests
    {
        private const double Ratio = 920.0 / 660.0;
        private const int MinWidth = 720;
        private const int MinHeight = 517;

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

        [Fact]
        public void AdjustResizeRectForAspectRatio_AnchorsOppositeCornerOnTopLeftDrag()
        {
            var rect = new MainForm.ResizeRect { Left = 50, Top = 80, Right = 1200, Bottom = 900 };
            MainForm.AdjustResizeRectForAspectRatio(ref rect, 4 /* WMSZ_TOPLEFT */, Ratio, MinWidth, MinHeight);

            Assert.Equal(1200, rect.Right);
            Assert.Equal(900, rect.Bottom);
        }
    }
}
