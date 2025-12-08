using System.Collections.Generic;

namespace VirtualKeyboard;

public class KeyboardLayout
{
    public string Name { get; set; }
    public Dictionary<string, KeyDefinition> Keys { get; set; } = new();

    public class KeyDefinition
    {
        public string Display { get; set; }
        public string DisplayShift { get; set; }
        public string Value { get; set; }
        public string ValueShift { get; set; }
        public bool IsLetter { get; set; }

        public KeyDefinition(string display, string value, bool isLetter = false)
        {
            Display = display;
            DisplayShift = isLetter ? display.ToUpper() : display;
            Value = value;
            ValueShift = isLetter ? value.ToUpper() : value;
            IsLetter = isLetter;
        }

        public KeyDefinition(string display, string displayShift, string value, string valueShift)
        {
            Display = display;
            DisplayShift = displayShift;
            Value = value;
            ValueShift = valueShift;
            IsLetter = false;
        }
    }

    public static KeyboardLayout CreateEnglishLayout()
    {
        var layout = new KeyboardLayout { Name = "English" };

        // Letters
        layout.Keys["q"] = new KeyDefinition("q", "q", true);
        layout.Keys["w"] = new KeyDefinition("w", "w", true);
        layout.Keys["e"] = new KeyDefinition("e", "e", true);
        layout.Keys["r"] = new KeyDefinition("r", "r", true);
        layout.Keys["t"] = new KeyDefinition("t", "t", true);
        layout.Keys["y"] = new KeyDefinition("y", "y", true);
        layout.Keys["u"] = new KeyDefinition("u", "u", true);
        layout.Keys["i"] = new KeyDefinition("i", "i", true);
        layout.Keys["o"] = new KeyDefinition("o", "o", true);
        layout.Keys["p"] = new KeyDefinition("p", "p", true);
        layout.Keys["a"] = new KeyDefinition("a", "a", true);
        layout.Keys["s"] = new KeyDefinition("s", "s", true);
        layout.Keys["d"] = new KeyDefinition("d", "d", true);
        layout.Keys["f"] = new KeyDefinition("f", "f", true);
        layout.Keys["g"] = new KeyDefinition("g", "g", true);
        layout.Keys["h"] = new KeyDefinition("h", "h", true);
        layout.Keys["j"] = new KeyDefinition("j", "j", true);
        layout.Keys["k"] = new KeyDefinition("k", "k", true);
        layout.Keys["l"] = new KeyDefinition("l", "l", true);
        layout.Keys["z"] = new KeyDefinition("z", "z", true);
        layout.Keys["x"] = new KeyDefinition("x", "x", true);
        layout.Keys["c"] = new KeyDefinition("c", "c", true);
        layout.Keys["v"] = new KeyDefinition("v", "v", true);
        layout.Keys["b"] = new KeyDefinition("b", "b", true);
        layout.Keys["n"] = new KeyDefinition("n", "n", true);
        layout.Keys["m"] = new KeyDefinition("m", "m", true);

        return layout;
    }

    public static KeyboardLayout CreateRussianLayout()
    {
        var layout = new KeyboardLayout { Name = "Russian" };

        // Row 2 - ЙЦУКЕН
        layout.Keys["q"] = new KeyDefinition("й", "й", true);
        layout.Keys["w"] = new KeyDefinition("ц", "ц", true);
        layout.Keys["e"] = new KeyDefinition("у", "у", true);
        layout.Keys["r"] = new KeyDefinition("к", "к", true);
        layout.Keys["t"] = new KeyDefinition("е", "е", true);
        layout.Keys["y"] = new KeyDefinition("н", "н", true);
        layout.Keys["u"] = new KeyDefinition("г", "г", true);
        layout.Keys["i"] = new KeyDefinition("ш", "ш", true);
        layout.Keys["o"] = new KeyDefinition("щ", "щ", true);
        layout.Keys["p"] = new KeyDefinition("з", "з", true);
        layout.Keys["["] = new KeyDefinition("х", "х", true);
        layout.Keys["]"] = new KeyDefinition("ъ", "ъ", true);

        // Row 3 - ФЫВАП
        layout.Keys["a"] = new KeyDefinition("ф", "ф", true);
        layout.Keys["s"] = new KeyDefinition("ы", "ы", true);
        layout.Keys["d"] = new KeyDefinition("в", "в", true);
        layout.Keys["f"] = new KeyDefinition("а", "а", true);
        layout.Keys["g"] = new KeyDefinition("п", "п", true);
        layout.Keys["h"] = new KeyDefinition("р", "р", true);
        layout.Keys["j"] = new KeyDefinition("о", "о", true);
        layout.Keys["k"] = new KeyDefinition("л", "л", true);
        layout.Keys["l"] = new KeyDefinition("д", "д", true);
        layout.Keys[";"] = new KeyDefinition("ж", "ж", true);
        layout.Keys["'"] = new KeyDefinition("э", "э", true);

        // Row 4 - ЯЧСМИТ
        layout.Keys["z"] = new KeyDefinition("я", "я", true);
        layout.Keys["x"] = new KeyDefinition("ч", "ч", true);
        layout.Keys["c"] = new KeyDefinition("с", "с", true);
        layout.Keys["v"] = new KeyDefinition("м", "м", true);
        layout.Keys["b"] = new KeyDefinition("и", "и", true);
        layout.Keys["n"] = new KeyDefinition("т", "т", true);
        layout.Keys["m"] = new KeyDefinition("ь", "ь", true);
        layout.Keys[","] = new KeyDefinition("б", "б", true);
        layout.Keys["."] = new KeyDefinition("ю", "ю", true);

        return layout;
    }

    public static KeyboardLayout CreateSymbolLayout()
    {
        var layout = new KeyboardLayout { Name = "Symbols" };

        // Row 2
        layout.Keys["q"] = new KeyDefinition("@", "@");
        layout.Keys["w"] = new KeyDefinition("&", "&");
        layout.Keys["e"] = new KeyDefinition("€", "€");
        layout.Keys["r"] = new KeyDefinition("#", "#");
        layout.Keys["t"] = new KeyDefinition("№", "№");
        layout.Keys["y"] = new KeyDefinition("_", "_");
        layout.Keys["u"] = new KeyDefinition("~", "~");
        layout.Keys["i"] = new KeyDefinition("∞", "∞");
        layout.Keys["o"] = new KeyDefinition("%", "%");
        layout.Keys["p"] = new KeyDefinition("÷", "÷");
        layout.Keys["["] = new KeyDefinition("×", "×");
        layout.Keys["]"] = new KeyDefinition("±", "±");

        // Row 3
        layout.Keys["a"] = new KeyDefinition("{", "{");
        layout.Keys["s"] = new KeyDefinition("}", "}");
        layout.Keys["d"] = new KeyDefinition("[", "[");
        layout.Keys["f"] = new KeyDefinition("]", "]");
        layout.Keys["g"] = new KeyDefinition("(", "(");
        layout.Keys["h"] = new KeyDefinition(")", ")");
        layout.Keys["j"] = new KeyDefinition("≤", "≤");
        layout.Keys["k"] = new KeyDefinition("≥", "≥");
        layout.Keys["l"] = new KeyDefinition("<", "<");
        layout.Keys[";"] = new KeyDefinition(">", ">");
        layout.Keys["'"] = new KeyDefinition("|", "|");

        // Row 4
        layout.Keys["z"] = new KeyDefinition("́", "́"); // Combining acute accent
        layout.Keys["x"] = new KeyDefinition("«", "«");
        layout.Keys["c"] = new KeyDefinition("»", "»");
        layout.Keys["v"] = new KeyDefinition(""", """);
        layout.Keys["b"] = new KeyDefinition(""", """);
        layout.Keys["n"] = new KeyDefinition("'", "'");
        layout.Keys["m"] = new KeyDefinition("'", "'");
        layout.Keys[","] = new KeyDefinition("^", "^");
        layout.Keys["."] = new KeyDefinition(";", ";");

        return layout;
    }
}