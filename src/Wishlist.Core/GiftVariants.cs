using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using Wishlist.Contracts;

namespace Wishlist.Core;

public partial class Gift
{
    public string? VariantsJson { get; private set; }
    public Guid? VariantsVersion { get; private set; }
    public string? SelectedVariantJson { get; private set; }

    [NotMapped]
    public GiftVariantDto[] Variants => VariantsJson == null
        ? [] : JsonSerializer.Deserialize<GiftVariantDto[]>(VariantsJson)!;

    private void EditVariants(GiftWriteInput input)
    {
        var current = Variants;
        if ((Unlimited || input.Unlimited) && (input.Variants?.Length > 0 ||
            current.Length > 0 || SelectedVariantJson != null))
            throw new RuleException("У бесконечного желания не может быть вариантов.", 400);
        if (input.Variants == null) return;
        if (input.Variants.Length > 0 && OhmywishesKey != null)
            throw new RuleException("Варианты доступны для желаний, созданных в Дарим.", 400);

        var known = current.Select(variant => variant.Id).ToHashSet();
        var used = new HashSet<Guid>();
        var next = input.Variants.Select(inputVariant =>
        {
            if (inputVariant == null || string.IsNullOrWhiteSpace(inputVariant.Title) ||
                inputVariant.Title.Trim().Length > 160)
                throw new RuleException("Название варианта: от 1 до 160 символов.", 400);
            if (inputVariant.Price is < 0 or > 100000000 ||
                inputVariant.Price is { } price && decimal.Round(price, 2) != price)
                throw new RuleException("Некорректная цена варианта.", 400);
            var url = string.IsNullOrWhiteSpace(inputVariant.Url) ? null : inputVariant.Url.Trim();
            if (url != null && (url.Length > 2048 || !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo)))
                throw new RuleException("Для варианта нужна ссылка http или https до 2048 символов.", 400);
            var description = string.IsNullOrWhiteSpace(inputVariant.Description) ? null : inputVariant.Description.Trim();
            if (description?.Length > 2000)
                throw new RuleException("Описание варианта должно быть не длиннее 2000 символов.", 400);
            var id = inputVariant.Id ?? Guid.NewGuid();
            if ((inputVariant.Id != null && !known.Contains(id)) || !used.Add(id))
                throw new RuleException("Список вариантов изменился. Откройте редактор снова.");
            return new GiftVariantDto(id, inputVariant.Title.Trim(), url, inputVariant.Price, description);
        }).ToArray();

        if (current.SequenceEqual(next)) return;
        VariantsJson = next.Length == 0 ? null : JsonSerializer.Serialize(next);
        VariantsVersion = next.Length == 0 ? null : Guid.NewGuid();
    }

    public GiftVariantDto? SelectVariant(Guid? variantId, Guid? variantsVersion)
    {
        if (VariantsVersion == null && variantsVersion == null && variantId == null) return null;
        if (VariantsVersion != null && variantsVersion == VariantsVersion &&
            Variants.FirstOrDefault(variant => variant.Id == variantId) is { } selected)
            return selected;
        throw new RuleException("Варианты желания изменились. Выберите вариант заново.");
    }

    public void ConfirmGuestReservation(Guid guestId, GiftVariantDto? variant)
    {
        Require(guestId != OwnerId, "Нельзя выбрать собственный подарок.", 403);
        Require(!Archived && !Bought && !Unlimited, "Подарок недоступен.");
        Reserve(guestId, variant);
    }

    private void Reserve(Guid userId, GiftVariantDto? variant)
    {
        Require(Mode == "free", "Подарок уже занят.");
        Snapshot(variant);
        Mode = "solo";
        DonorId = userId;
    }

    public static GiftVariantDto? ReadVariant(string? json) =>
        json == null ? null : JsonSerializer.Deserialize<GiftVariantDto>(json);
}
