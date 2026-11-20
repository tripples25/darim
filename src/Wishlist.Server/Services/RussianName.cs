namespace Wishlist.Server.Services;

public static class RussianName
{
    private static readonly Dictionary<string, string> Feminine = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Алёна"] = "Алёны", ["Алена"] = "Алены", ["Александра"] = "Александры", ["Алина"] = "Алины",
        ["Алиса"] = "Алисы", ["Алла"] = "Аллы", ["Анастасия"] = "Анастасии", ["Анна"] = "Анны", ["Аня"] = "Ани",
        ["Валерия"] = "Валерии", ["Варвара"] = "Варвары", ["Вера"] = "Веры", ["Виктория"] = "Виктории",
        ["Галина"] = "Галины", ["Дарья"] = "Дарьи", ["Диана"] = "Дианы", ["Ева"] = "Евы",
        ["Евгения"] = "Евгении", ["Екатерина"] = "Екатерины", ["Елена"] = "Елены", ["Елизавета"] = "Елизаветы",
        ["Ирина"] = "Ирины", ["Карина"] = "Карины", ["Кристина"] = "Кристины", ["Ксения"] = "Ксении",
        ["Лидия"] = "Лидии", ["Любовь"] = "Любови", ["Маргарита"] = "Маргариты", ["Марина"] = "Марины",
        ["Мария"] = "Марии", ["Маша"] = "Маши", ["Надежда"] = "Надежды", ["Наталия"] = "Наталии",
        ["Наталья"] = "Натальи", ["Оксана"] = "Оксаны", ["Ольга"] = "Ольги", ["Полина"] = "Полины",
        ["Светлана"] = "Светланы", ["София"] = "Софии", ["Софья"] = "Софьи", ["Татьяна"] = "Татьяны",
        ["Юлия"] = "Юлии", ["Юля"] = "Юли", ["Яна"] = "Яны"
    };

    private static readonly Dictionary<string, string> Masculine = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Александр"] = "Александра", ["Алексей"] = "Алексея", ["Анатолий"] = "Анатолия", ["Андрей"] = "Андрея",
        ["Антон"] = "Антона", ["Артем"] = "Артема", ["Артём"] = "Артёма", ["Борис"] = "Бориса",
        ["Вадим"] = "Вадима", ["Валерий"] = "Валерия", ["Василий"] = "Василия", ["Виктор"] = "Виктора",
        ["Виталий"] = "Виталия", ["Владимир"] = "Владимира", ["Владислав"] = "Владислава", ["Даниил"] = "Даниила",
        ["Денис"] = "Дениса", ["Дмитрий"] = "Дмитрия", ["Евгений"] = "Евгения", ["Егор"] = "Егора",
        ["Иван"] = "Ивана", ["Игорь"] = "Игоря", ["Илья"] = "Ильи", ["Кирилл"] = "Кирилла",
        ["Константин"] = "Константина", ["Лев"] = "Льва", ["Макс"] = "Макса", ["Максим"] = "Максима",
        ["Марк"] = "Марка", ["Матвей"] = "Матвея", ["Михаил"] = "Михаила", ["Никита"] = "Никиты",
        ["Николай"] = "Николая", ["Олег"] = "Олега", ["Павел"] = "Павла", ["Пётр"] = "Петра",
        ["Петр"] = "Петра", ["Роман"] = "Романа", ["Сергей"] = "Сергея", ["Степан"] = "Степана",
        ["Фёдор"] = "Фёдора", ["Юрий"] = "Юрия", ["Ярослав"] = "Ярослава"
    };

    private static readonly Dictionary<string, string> ShortNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Саша"] = "Саши", ["Женя"] = "Жени", ["Валя"] = "Вали", ["Лёша"] = "Лёши", ["Леша"] = "Леши",
        ["Дима"] = "Димы", ["Паша"] = "Паши", ["Миша"] = "Миши", ["Ваня"] = "Вани"
    };

    public static string Genitive(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > 2) return name;
        var first = parts[0];
        var feminine = Feminine.TryGetValue(first, out var declined);
        if (!feminine && !Masculine.TryGetValue(first, out declined))
            return parts.Length == 1 && ShortNames.TryGetValue(first, out declined) ? MatchCase(first, declined) : name;
        if (parts.Length == 1) return MatchCase(first, declined!);

        var surname = parts[1];
        if (surname.Any(c => c is not (>= 'а' and <= 'я' or >= 'А' and <= 'Я' or 'ё' or 'Ё')))
            return name;
        var ending = surname.ToLowerInvariant();
        string declinedSurname;
        if (feminine && ending.EndsWith("ская")) declinedSurname = surname[..^2] + "ой";
        else if (feminine && (ending.EndsWith("ова") || ending.EndsWith("ева") || ending.EndsWith("ина")))
            declinedSurname = surname[..^1] + "ой";
        else if (!feminine && ending.EndsWith("ский")) declinedSurname = surname[..^2] + "ого";
        else if (!feminine && (ending.EndsWith("ов") || ending.EndsWith("ев") || ending.EndsWith("ин")))
            declinedSurname = surname + "а";
        else if (ending.EndsWith("ко") || ending.EndsWith("ых") || ending.EndsWith("их")) declinedSurname = surname;
        else return name;
        return $"{MatchCase(first, declined!)} {MatchCase(surname, declinedSurname)}";
    }

    private static string MatchCase(string original, string value) => original.All(char.IsUpper)
        ? value.ToUpperInvariant()
        : char.IsLower(original[0]) ? value.ToLowerInvariant() : value;
}
