using System;

namespace SplatPreprocess
{
    [Flags]
    public enum Stage1ButtonPress { None = 0, Bookmark = 1, ToggleOriginal = 2 }

    public sealed class Stage1ButtonEdges
    {
        bool previousA, previousB;

        public Stage1ButtonPress Sample(bool rightA, bool rightB)
        {
            var press = Stage1ButtonPress.None;
            if (rightA && !previousA) press |= Stage1ButtonPress.Bookmark;
            if (rightB && !previousB) press |= Stage1ButtonPress.ToggleOriginal;
            previousA = rightA;
            previousB = rightB;
            return press;
        }
    }
}
