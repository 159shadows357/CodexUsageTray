namespace CodexUsageTray;

internal sealed class BufferedFlowLayoutPanel : FlowLayoutPanel
{
    public BufferedFlowLayoutPanel()
    {
        // Buffer painting without changing native WS_VISIBLE on the popup window.
        DoubleBuffered = true;
        ResizeRedraw = true;
    }
}
