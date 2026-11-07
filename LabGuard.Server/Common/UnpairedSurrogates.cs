namespace LabGuard.Server.Common;

// A UTF-16 surrogate without its partner. Such text can't be encoded as UTF-8 and can't be shown on screen.
public static class UnpairedSurrogates
{
    public static bool IsPresentIn(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            if (char.IsLowSurrogate(text[index]))
            {
                return true;
            }

            if (!char.IsHighSurrogate(text[index]))
            {
                continue;
            }

            if (index + 1 == text.Length || !char.IsLowSurrogate(text[index + 1]))
            {
                return true;
            }

            index++;
        }

        return false;
    }
}
