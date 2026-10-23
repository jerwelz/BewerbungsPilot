using System.Security.Cryptography;
using System.Text;
using BewerbungsPilot.Data;

namespace BewerbungsPilot.Logic;

// Fictional example data for one demo account (DemoAccounts.cs). All companies, people and
// documents are invented. Dates are relative to "today", so deadlines and interviews are
// always upcoming and the history reaches back about three months.
// The context must belong to the demo user: the ownership guard checks every row.
public class DemoSeeder
{
    private readonly AppDbContext _db;
    private readonly DocumentStorage _storage;
    private readonly TimeProvider _clock;

    public DemoSeeder(AppDbContext db, DocumentStorage storage, TimeProvider clock)
    {
        _db = db;
        _storage = storage;
        _clock = clock;
    }

    // One fictional application. Steps: (status, days ago) in order; the first step is the creation.
    private sealed record Sample(
        string Company, string Position, string Location, (double Lat, double Lon)? Map, LocationPrecision Precision,
        (ApplicationStatus Status, int DaysAgo)[] Steps, int? DeadlineInDays, string? Notes);

    private const string Azubi = "Ausbildung Fachinformatiker/in für Anwendungsentwicklung (m/w/d)";

    private static readonly Sample[] Samples =
    [
        new("Elbstrom Software AG", Azubi, "Hamburg", (53.5511, 9.9937), LocationPrecision.City,
            [(ApplicationStatus.Entwurf, 70), (ApplicationStatus.Beworben, 66), (ApplicationStatus.InPruefung, 52), (ApplicationStatus.Vorstellungsgespraech, 38), (ApplicationStatus.Angebot, 9)],
            null, "Zweites Gespräch mit dem Ausbildungsleiter war sehr positiv. Vertrag bis Monatsende prüfen."),
        new("Kranichfeld Digital GmbH", Azubi, "Berlin", (52.5200, 13.4050), LocationPrecision.City,
            [(ApplicationStatus.Beworben, 45), (ApplicationStatus.InPruefung, 31), (ApplicationStatus.Vorstellungsgespraech, 12)],
            null, "Video-Gespräch vorbereiten: Projekt aus der Schule erklären können."),
        new("Lindenhof IT-Systeme GmbH", Azubi, "Hannover", (52.3759, 9.7320), LocationPrecision.City,
            [(ApplicationStatus.Beworben, 40), (ApplicationStatus.InPruefung, 25), (ApplicationStatus.Vorstellungsgespraech, 6)],
            null, "Vor-Ort-Termin mit kleiner Programmieraufgabe (ca. 30 Minuten)."),
        new("Fjordblau Cloud GmbH", "Ausbildung Fachinformatiker/in für Systemintegration (m/w/d)", "Kiel", (54.3233, 10.1228), LocationPrecision.City,
            [(ApplicationStatus.Beworben, 34), (ApplicationStatus.InPruefung, 18)],
            null, "Rückmeldung laut Telefonat in ca. zwei Wochen."),
        new("Uhlenhorst Health Tech GmbH", Azubi, "Bremen", (53.0793, 8.8017), LocationPrecision.City,
            [(ApplicationStatus.Beworben, 27), (ApplicationStatus.InPruefung, 8)],
            null, null),
        new("Rabenwerk Logistik-IT GmbH", Azubi, "Leipzig", (51.3397, 12.3731), LocationPrecision.City,
            [(ApplicationStatus.Beworben, 21)],
            null, "Online-Formular, Eingangsbestätigung per E-Mail erhalten."),
        new("Moosgrün Medientechnik GmbH", "Ausbildung Kaufmann/-frau für Digitalisierungsmanagement (m/w/d)", "Köln", (50.9375, 6.9603), LocationPrecision.City,
            [(ApplicationStatus.Beworben, 16)],
            null, null),
        new("Taubenschlag Games GmbH", Azubi, "Remote / Frankfurt am Main", null, LocationPrecision.Remote,
            [(ApplicationStatus.Beworben, 4)],
            null, "Ausbildung mit Berufsschule in Frankfurt, Arbeit überwiegend remote."),
        new("Sonnenhang Versicherungs-IT GmbH", Azubi, "München", (48.1351, 11.5820), LocationPrecision.City,
            [(ApplicationStatus.Entwurf, 5)],
            9, "Anschreiben noch an die Stellenanzeige anpassen."),
        new("Kieselstein Stadtwerke Digital GmbH", "Duales Studium Wirtschaftsinformatik (m/w/d)", "Lübeck", (53.8655, 10.6866), LocationPrecision.City,
            [(ApplicationStatus.Entwurf, 2)],
            16, "Bewerbungsschluss beachten, Zeugniskopie beilegen."),
        new("Weidenbach Automotive Software GmbH", Azubi, "Stuttgart", (48.7758, 9.1829), LocationPrecision.City,
            [(ApplicationStatus.Beworben, 80), (ApplicationStatus.InPruefung, 64), (ApplicationStatus.Absage, 50)],
            null, "Absage mit Hinweis: Stelle intern besetzt. Im nächsten Jahr erneut bewerben?"),
        new("Brückenwerk Bank-IT GmbH", Azubi, "Frankfurt am Main", (50.1109, 8.6821), LocationPrecision.City,
            [(ApplicationStatus.Beworben, 60), (ApplicationStatus.InPruefung, 47), (ApplicationStatus.Vorstellungsgespraech, 35), (ApplicationStatus.Absage, 24)],
            null, "Feedback: fachlich gut, Mitbewerber hatte schon Praktikumserfahrung."),
    ];

    public async Task SeedAsync()
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var localNow = _clock.GetLocalNow().DateTime;
        var today = DateOnly.FromDateTime(localNow);
        DateTime DaysAgo(int days) => now.AddDays(-days);

        var applications = new Dictionary<string, JobApplication>();
        foreach (var sample in Samples)
        {
            var created = DaysAgo(sample.Steps[0].DaysAgo);
            var last = sample.Steps[^1];
            var applied = sample.Steps.FirstOrDefault(s => s.Status == ApplicationStatus.Beworben);
            var application = new JobApplication
            {
                CompanyName = sample.Company,
                Position = sample.Position,
                Location = sample.Location,
                JobUrl = $"https://example.com/stellen/{Slug(sample.Company)}",
                Status = last.Status,
                AppliedOn = applied == default ? null : DateOnly.FromDateTime(localNow.AddDays(-applied.DaysAgo)),
                Deadline = sample.DeadlineInDays is { } inDays ? today.AddDays(inDays) : null,
                Notes = sample.Notes,
                CreatedAt = created,
                UpdatedAt = DaysAgo(last.DaysAgo),
                StatusChangedAt = DaysAgo(last.DaysAgo),
                LocationPrecision = sample.Precision,
                MapLatitude = sample.Map?.Lat,
                MapLongitude = sample.Map?.Lon,
                MapLabel = sample.Precision == LocationPrecision.Remote ? null : sample.Location,
            };
            application.Events.Add(new ApplicationEvent
            {
                Type = EventType.Created,
                ToStatus = sample.Steps[0].Status,
                Description = $"Bewerbung angelegt (Status: {StatusLabels.German[sample.Steps[0].Status]})",
                OccurredAt = created
            });
            for (var i = 1; i < sample.Steps.Length; i++)
            {
                var (from, to) = (sample.Steps[i - 1].Status, sample.Steps[i].Status);
                application.Events.Add(new ApplicationEvent
                {
                    Type = EventType.StatusChanged,
                    FromStatus = from,
                    ToStatus = to,
                    Description = $"Status geändert: {StatusLabels.German[from]} → {StatusLabels.German[to]}",
                    OccurredAt = DaysAgo(sample.Steps[i].DaysAgo)
                });
            }
            applications[sample.Company] = application;
            _db.Applications.Add(application);
        }

        // Interviews: completed ones in the past, planned ones in the coming days (local wall-clock time).
        var morning = localNow.Date.AddHours(10);
        AddInterview(applications["Elbstrom Software AG"], morning.AddDays(-38), InterviewType.Video, InterviewStatus.Abgeschlossen, "Video-Link per E-Mail", "Frau Beispiel (Ausbildungsleitung)", DaysAgo(40));
        AddInterview(applications["Elbstrom Software AG"], morning.AddDays(-16).AddHours(4), InterviewType.VorOrt, InterviewStatus.Abgeschlossen, "Musterallee 1, Hamburg", "Herr Muster (Teamleitung)", DaysAgo(30));
        AddInterview(applications["Kranichfeld Digital GmbH"], morning.AddDays(3), InterviewType.Video, InterviewStatus.Geplant, "https://example.com/meeting", "Frau Exempel (HR)", DaysAgo(12));
        AddInterview(applications["Lindenhof IT-Systeme GmbH"], morning.AddDays(8).AddHours(4).AddMinutes(30), InterviewType.VorOrt, InterviewStatus.Geplant, "Beispielweg 5, Hannover", "Herr Platzhalter (Ausbildung)", DaysAgo(6));
        AddInterview(applications["Fjordblau Cloud GmbH"], morning.AddDays(1).AddHours(-1), InterviewType.Telefon, InterviewStatus.Geplant, null, "Frau Fiktiv (Personal)", DaysAgo(2));
        AddInterview(applications["Brückenwerk Bank-IT GmbH"], morning.AddDays(-33), InterviewType.VorOrt, InterviewStatus.Abgeschlossen, "Beispielplatz 3, Frankfurt am Main", null, DaysAgo(35));

        // Two generated placeholder PDFs (no real documents).
        await AddDocumentAsync(applications["Elbstrom Software AG"], DocumentCategory.Lebenslauf, "Lebenslauf_Beispiel.pdf",
            ["Lebenslauf (Beispieldokument, fiktiv)", "", "Max Mustermann", "Musterstraße 1, 12345 Musterstadt", "",
             "Schule: Fachoberschule Informatik (Beispiel)", "Praktikum: 3 Wochen Webentwicklung (Beispiel)",
             "Kenntnisse: C#, SQL, HTML/CSS, JavaScript"], DaysAgo(66));
        await AddDocumentAsync(applications["Kranichfeld Digital GmbH"], DocumentCategory.Anschreiben, "Anschreiben_Kranichfeld.pdf",
            ["Anschreiben (Beispieldokument, fiktiv)", "", "Bewerbung um einen Ausbildungsplatz als",
             "Fachinformatiker für Anwendungsentwicklung", "", "Sehr geehrte Damen und Herren,",
             "dieser Text ist ein Platzhalter für die Demo von BewerbungsPilot."], DaysAgo(45));

        await _db.SaveChangesAsync();
    }

    private static void AddInterview(JobApplication application, DateTime scheduledAt, InterviewType type, InterviewStatus status,
        string? location, string? contact, DateTime recordedAt)
    {
        application.Interviews.Add(new Interview
        {
            ScheduledAt = scheduledAt,
            Type = type,
            Status = status,
            Location = location,
            ContactPerson = contact,
            CreatedAt = recordedAt,
            UpdatedAt = recordedAt
        });
        var what = $"Vorstellungsgespräch ({StatusLabels.InterviewTypes[type]}) am {ScheduleRules.FormatDateTime(scheduledAt)}";
        application.Events.Add(new ApplicationEvent
        {
            Type = EventType.InterviewScheduled,
            Description = status == InterviewStatus.Abgeschlossen ? $"{what} erfasst (abgeschlossen)" : $"{what} geplant",
            OccurredAt = recordedAt
        });
    }

    private async Task AddDocumentAsync(JobApplication application, DocumentCategory category, string fileName, string[] lines, DateTime uploadedAt)
    {
        var content = SimplePdf(lines);
        application.Documents.Add(new ApplicationDocument
        {
            Category = category,
            OriginalFileName = fileName,
            StoredFileName = await _storage.SaveAsync(content, ".pdf"),
            ContentType = "application/pdf",
            SizeBytes = content.Length,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(content)),
            UploadedAt = uploadedAt
        });
        application.Events.Add(new ApplicationEvent
        {
            Type = EventType.DocumentAdded,
            Description = $"Dokument hinzugefügt: {category} ({fileName})",
            OccurredAt = uploadedAt
        });
    }

    private static string Slug(string company) =>
        new string(company.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');

    // A minimal one-page PDF with a few lines of text (Helvetica, Windows-1252 for umlauts).
    public static byte[] SimplePdf(string[] lines)
    {
        var latin1 = Encoding.Latin1;
        var text = new StringBuilder("BT /F1 13 Tf 72 760 Td 18 TL\n");
        foreach (var line in lines)
            text.Append('(').Append(line.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)")).Append(") Tj T*\n");
        text.Append("ET");
        var stream = text.ToString();

        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            $"<< /Length {latin1.GetByteCount(stream)} >>\nstream\n{stream}\nendstream",
        ];

        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(latin1.GetByteCount(pdf.ToString()));
            pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = latin1.GetByteCount(pdf.ToString());
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            pdf.Append($"{offset:D10} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return latin1.GetBytes(pdf.ToString());
    }
}
