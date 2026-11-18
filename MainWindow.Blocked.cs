using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace DamaCapture;

/// <summary>
/// Some windows ask Windows to keep them out of screenshots (protected video, banking and some messengers). Their area
/// comes out black or shows what is behind them. The capture marks those places so the gap is not taken for a fault;
/// nothing is done to get around the protection.
/// </summary>
internal sealed partial class MainWindow
{
    private void ShowBlockedAreas(IReadOnlyList<System.Drawing.Rectangle> areas)
    {
        surface.Blocked = areas.Select(area => new Rect(area.X, area.Y, area.Width, area.Height)).ToArray();
        if (areas.Count > 0) Notify("캡처를 막은 창이 있어 그 부분은 찍히지 않았습니다");
    }
}
