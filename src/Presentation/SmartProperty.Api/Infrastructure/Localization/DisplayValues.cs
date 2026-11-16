#nullable enable
using System.Collections.Frozen;
using System.Text;

namespace SmartProperty.Api.Infrastructure.Localization;

/// <summary>
/// English and Arabic labels for the closed-list values the API returns (statuses, property types, kinds, purposes,
/// periods, document types, ...). Values are matched ignoring case, spaces and symbols, so the AI search service's
/// "Semi Finished" or "Core &amp; Shell" find the same label as this platform's <c>SemiFinished</c> and
/// <c>CoreAndShell</c>. A value with no label is shown as its words ("PendingReview" becomes "Pending review").
/// </summary>
public static class DisplayValues
{
    private static readonly FrozenDictionary<string, (string En, string Ar)> Labels = new (string Value, string En, string Ar)[]
    {
        // Workflow statuses
        ("Draft", "Draft", "مسودة"),
        ("Pending", "Pending", "قيد الانتظار"),
        ("PendingReview", "Pending review", "قيد المراجعة"),
        ("PendingLegalReview", "Pending legal review", "بانتظار المراجعة القانونية"),
        ("PendingIssuance", "Pending issuance", "بانتظار الإصدار"),
        ("PendingScan", "Pending scan", "بانتظار الفحص"),
        ("PendingSignature", "Pending signature", "بانتظار التوقيع"),
        ("UnderReview", "Under review", "قيد المراجعة"),
        ("ChangesRequested", "Changes requested", "مطلوب تعديلات"),
        ("ChangesRequired", "Changes required", "يتطلب تعديلات"),
        ("NotStarted", "Not started", "لم يبدأ"),
        ("Submitted", "Submitted", "مُقدَّم"),
        ("Approved", "Approved", "معتمد"),
        ("Accepted", "Accepted", "مقبول"),
        ("Rejected", "Rejected", "مرفوض"),
        ("Withdrawn", "Withdrawn", "مسحوب"),
        ("WithdrawnByRequester", "Withdrawn by requester", "سحبه مقدم الطلب"),
        ("ClosedListingUnavailable", "Closed: listing unavailable", "مغلق: الإعلان غير متاح"),
        ("Superseded", "Superseded", "تم استبداله"),
        ("Active", "Active", "نشط"),
        ("Archived", "Archived", "مؤرشف"),
        ("Suspended", "Suspended", "موقوف"),
        ("Deactivated", "Deactivated", "معطّل"),
        ("Revoked", "Revoked", "ملغى"),
        ("Expired", "Expired", "منتهي الصلاحية"),
        ("Open", "Open", "متاح"),
        ("Reserved", "Reserved", "محجوز"),
        ("Closed", "Closed", "مغلق"),
        ("Published", "Published", "منشور"),
        ("Unpublished", "Unpublished", "غير منشور"),
        ("Signed", "Signed", "موقّع"),
        ("Occupied", "Occupied", "مشغول"),
        ("Completed", "Completed", "مكتمل"),
        ("Eligible", "Eligible", "مؤهل"),
        ("NotEligible", "Not eligible", "غير مؤهل"),
        ("Unavailable", "Unavailable", "غير متاح"),
        ("Available", "Available", "متاح"),
        ("Sold", "Sold", "مباع"),
        ("Rented", "Rented", "مؤجَّر"),
        ("Found", "Found", "موجود"),
        ("NotFound", "Not found", "غير موجود"),

        // Relisting a closed listing
        ("Manual", "Manual", "يدوي"),
        ("Automatic", "Automatic", "تلقائي"),

        // Document scanning
        ("Clean", "Clean", "سليم"),
        ("Infected", "Infected", "مصاب ببرمجية ضارة"),
        ("ScanFailed", "Scan failed", "فشل الفحص"),
        ("Failed", "Failed", "فشل"),
        ("Error", "Error", "خطأ"),
        ("Unknown", "Unknown", "غير معروف"),

        // Authority request decisions and commitment steps
        ("LegalApproved", "Approved by legal reviewer", "اعتمده المراجع القانوني"),
        ("LegalCompletionRequested", "Completion requested by legal reviewer", "طلب المراجع القانوني استكمال البيانات"),
        ("LegalRejected", "Rejected by legal reviewer", "رفضه المراجع القانوني"),
        ("AdminApproved", "Approved by admin", "اعتمده المسؤول"),
        ("AdminCompletionRequested", "Completion requested by admin", "طلب المسؤول استكمال البيانات"),
        ("AdminRejected", "Rejected by admin", "رفضه المسؤول"),
        ("ClosedOwnershipTransferred", "Closed: ownership transferred", "أُغلق: تم نقل الملكية"),

        // Kinds of authority request
        ("Listing", "Listing", "إعلان"),
        ("OwnershipTransfer", "Ownership transfer", "نقل ملكية"),
        ("Handover", "Handover", "التسليم"),
        ("Return", "Return", "الإرجاع"),

        // Authority documents, requirements and missing items
        ("TitleDeed", "Title deed", "سند الملكية"),
        ("SaleContract", "Sale contract", "عقد البيع"),
        ("UtilityBill", "Utility bill", "فاتورة مرافق"),
        ("Authorization", "Authorization", "توكيل"),
        ("BrokerageLicense", "Brokerage license", "رخصة الوساطة العقارية"),
        ("Other", "Other", "أخرى"),
        ("OneOfOwnershipProof", "One of the ownership proofs", "أحد مستندات إثبات الملكية"),
        ("RequiredForRepresentative", "Required for a representative", "مطلوب للممثل"),
        ("Optional", "Optional", "اختياري"),
        ("Details", "Details", "التفاصيل"),
        ("SaleListing", "Sale listing", "إعلان البيع"),
        ("RentListing", "Rent listing", "إعلان الإيجار"),
        ("OwnershipProof", "Ownership proof", "إثبات الملكية"),
        ("DocumentScanPending", "A document is waiting for its scan", "مستند بانتظار الفحص"),
        ("DocumentNotClean", "A document did not pass the scan", "مستند لم يجتز الفحص"),

        // Owners, representatives and roles
        ("Owner", "Owner", "المالك"),
        ("Individual", "Individual", "فرد"),
        ("Organization", "Organization", "مؤسسة"),
        ("IndividualRepresentative", "Individual representative", "ممثل فردي"),
        ("WorkspaceRepresentative", "Workspace representative", "ممثل عن مساحة عمل"),
        ("Customer", "Customer", "العميل"),
        ("Member", "Member", "عضو"),
        ("Platform", "Platform", "المنصة"),
        ("Workspace", "Workspace", "مساحة العمل"),
        ("Platform Admin", "Platform admin", "مسؤول المنصة"),
        ("Legal Reviewer", "Legal reviewer", "المراجع القانوني"),
        ("Property Owner", "Property owner", "مالك عقار"),
        ("Workspace Admin", "Workspace admin", "مسؤول مساحة العمل"),

        // Purposes, request kinds and rent periods
        ("Sale", "Sale", "بيع"),
        ("Rent", "Rent", "إيجار"),
        ("Purchase", "Purchase", "شراء"),
        ("Rental", "Rental", "إيجار"),
        ("Daily", "Daily", "يومي"),
        ("Weekly", "Weekly", "أسبوعي"),
        ("Monthly", "Monthly", "شهري"),
        ("Yearly", "Yearly", "سنوي"),

        // Finishing
        ("Finished", "Finished", "تشطيب كامل"),
        ("LuxuryFinished", "Luxury finished", "تشطيب فاخر"),
        ("SemiFinished", "Semi finished", "نصف تشطيب"),
        ("CoreAndShell", "Core and shell", "على الهيكل"),

        // Property types
        ("Land", "Land", "أرض"),
        ("Building", "Building", "مبنى"),
        ("Apartment", "Apartment", "شقة"),
        ("Villa", "Villa", "فيلا"),
        ("Duplex", "Duplex", "دوبلكس"),
        ("Studio", "Studio", "استوديو"),
        ("Chalet", "Chalet", "شاليه"),
        ("Townhouse", "Townhouse", "تاون هاوس"),
        ("TwinHouse", "Twin house", "توين هاوس"),
        ("Penthouse", "Penthouse", "بنتهاوس"),
        ("StandaloneVilla", "Standalone villa", "فيلا مستقلة"),
        ("HotelApartment", "Hotel apartment", "شقة فندقية"),
        ("Office", "Office", "مكتب"),
        ("Shop", "Shop", "محل تجاري"),
        ("Warehouse", "Warehouse", "مستودع"),
        ("Farm", "Farm", "مزرعة"),

        // Location levels
        ("Country", "Country", "الدولة"),
        ("City", "City", "المدينة"),
        ("Area", "Area", "المنطقة"),
        ("Compound", "Compound", "الكمبوند"),

        // One-time code purposes
        ("PhoneVerification", "Phone verification", "التحقق من رقم الهاتف"),
        ("EmailVerification", "Email verification", "التحقق من البريد الإلكتروني"),
        ("PasswordReset", "Password reset", "إعادة تعيين كلمة المرور"),
    }.ToFrozenDictionary(label => Normalize(label.Value), label => (label.En, label.Ar), StringComparer.Ordinal);

    /// <summary>The label of <paramref name="value"/> in the current language; null for null.</summary>
    public static string? Display(string? value)
    {
        if (value is null)
        {
            return null;
        }

        if (Labels.TryGetValue(Normalize(value), out var label))
        {
            return DisplayLanguage.IsArabic ? label.Ar : label.En;
        }

        return Words(value);
    }

    /// <summary>True when there is an English and Arabic label for <paramref name="value"/>.</summary>
    public static bool IsKnown(string value) => Labels.ContainsKey(Normalize(value));

    private static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Replace("&", "and", StringComparison.Ordinal))
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }

    /// <summary>"PendingReview" and "pending_review" both become "Pending review".</summary>
    private static string Words(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0 || trimmed.Contains(' ', StringComparison.Ordinal))
        {
            return trimmed;
        }

        var builder = new StringBuilder(trimmed.Length + 8);
        for (var index = 0; index < trimmed.Length; index++)
        {
            var character = trimmed[index];
            if (character is '_' or '-')
            {
                builder.Append(' ');
                continue;
            }

            if (index > 0 && char.IsUpper(character) && char.IsLower(trimmed[index - 1]))
            {
                builder.Append(' ');
            }

            builder.Append(builder.Length == 0 ? char.ToUpperInvariant(character) : char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }
}
