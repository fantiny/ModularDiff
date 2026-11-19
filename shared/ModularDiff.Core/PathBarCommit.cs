namespace ModularDiff.Core;

/// <summary>Validates left/right path-bar text for an immediate re-compare (Enter).</summary>
public static class PathBarCommit
{
    public static bool TryResolve(string? leftText, string? rightText, out string leftFull, out string rightFull, out string? error)
    {
        leftFull = ""; rightFull = ""; error = null;
        if (string.IsNullOrWhiteSpace(leftText) || string.IsNullOrWhiteSpace(rightText))
        {
            error = "请填写左右两侧路径。";
            return false;
        }
        try
        {
            leftFull = Path.GetFullPath(leftText.Trim());
            rightFull = Path.GetFullPath(rightText.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = "路径不合法：" + ex.Message;
            return false;
        }

        bool leftFile = File.Exists(leftFull), leftDir = Directory.Exists(leftFull);
        bool rightFile = File.Exists(rightFull), rightDir = Directory.Exists(rightFull);
        if (!leftFile && !leftDir) { error = "左侧路径不存在：" + leftFull; return false; }
        if (!rightFile && !rightDir) { error = "右侧路径不存在：" + rightFull; return false; }
        if (leftFile != rightFile || leftDir != rightDir)
        {
            error = "左右须同为文件或同为目录。";
            return false;
        }
        return true;
    }
}
