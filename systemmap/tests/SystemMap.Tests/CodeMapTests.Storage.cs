namespace SystemMap.Tests;

/// <summary>Lagring: Azure Blob Storage och filsystemet. Operationerna väljs efter metodens namn i en tabell (kod i minnet).</summary>
public partial class CodeMapTests
{
    private const string Container = "Azure.Storage.Blobs.BlobContainerClient _c = new Azure.Storage.Blobs.BlobServiceClient().GetBlobContainerClient(\"kvitton\");";
    private const string Service = "Azure.Storage.Blobs.BlobServiceClient _s = new();";

    [TestCase("await _c.GetBlobClient(\"a\").ExistsAsync();", "storage:kvitton | finns", TestName = "Blob finns, async")]
    [TestCase("_c.GetBlobClient(\"a\").Exists();", "storage:kvitton | finns", TestName = "Blob finns")]
    [TestCase("await _c.ExistsAsync();", "storage:kvitton | finns", TestName = "Containern finns")]
    [TestCase("await _c.GetBlobClient(\"a\").DeleteIfExistsAsync();", "storage:kvitton | ta bort", TestName = "Blob ta bort om den finns")]
    [TestCase("await _c.DeleteIfExistsAsync();", "storage:kvitton | ta bort", TestName = "Containern tas bort")]
    [TestCase("await _c.DeleteBlobIfExistsAsync(\"a\");", "storage:kvitton | ta bort", TestName = "Ta bort blob via containern")]
    [TestCase("await _c.GetBlobClient(\"a\").GetPropertiesAsync();", "storage:kvitton | läsa egenskaper", TestName = "Läsa egenskaper")]
    [TestCase("await _c.GetBlobClient(\"a\").SetMetadataAsync(new Dictionary<string, string>());", "storage:kvitton | ändra egenskaper", TestName = "Ändra metadata")]
    [TestCase("await _c.GetBlobClient(\"a\").OpenReadAsync();", "storage:kvitton | hämta", TestName = "Öppna för läsning")]
    [TestCase("await _c.GetBlobClient(\"a\").OpenWriteAsync(true);", "storage:kvitton | skriva", TestName = "Öppna för skrivning")]
    [TestCase("await _c.GetBlobClient(\"a\").DownloadContentAsync();", "storage:kvitton | hämta", TestName = "Ladda ned innehåll")]
    [TestCase("await _c.GetBlobClient(\"a\").DownloadStreamingAsync();", "storage:kvitton | hämta", TestName = "Ladda ned som ström")]
    [TestCase("await _c.GetBlobClient(\"a\").DownloadToAsync(System.IO.Stream.Null);", "storage:kvitton | hämta", TestName = "Ladda ned till ström")]
    [TestCase("await _c.GetBlobClient(\"a\").UploadAsync(new object());", "storage:kvitton | ladda upp", TestName = "Ladda upp")]
    [TestCase("await _c.GetBlobClient(\"a\").StartCopyFromUriAsync(new Uri(\"https://x.example/a\"));", "storage:kvitton | kopiera", TestName = "Börja kopiera")]
    [TestCase("await _c.GetBlobClient(\"a\").SyncCopyFromUriAsync(new Uri(\"https://x.example/a\"));", "storage:kvitton | kopiera", TestName = "Kopiera direkt")]
    [TestCase("await foreach (var b in _c.GetBlobsAsync()) { }", "storage:kvitton | lista", TestName = "Lista blobbar")]
    [TestCase("await _c.CreateIfNotExistsAsync();", "storage:kvitton | skapa", TestName = "Skapa containern")]
    public async Task Blob_operations_are_named_after_what_they_do_with_the_storage(string body, string expected)
    {
        var map = await MapAsync(Controller(Container, body));
        Assert.That(Uses(map.EntryPoints.Single()), Is.EqualTo(new[] { expected }));
    }

    [TestCase("await foreach (var c in _s.GetBlobContainersAsync()) { }", "storage:Blob | lista", TestName = "Tjänsten listar containrar")]
    [TestCase("await _s.CreateBlobContainerAsync(\"nya\");", "storage:nya | skapa", TestName = "Tjänsten skapar en container, som heter som namnet")]
    [TestCase("await _s.DeleteBlobContainerAsync(\"gamla\");", "storage:gamla | ta bort", TestName = "Tjänsten tar bort en container, som heter som namnet")]
    public async Task Blob_service_client_operations_are_recognized(string body, string expected)
    {
        var map = await MapAsync(Controller(Service, body));
        Assert.That(Uses(map.EntryPoints.Single()), Is.EqualTo(new[] { expected }));
    }

    [Test]
    public async Task Blob_near_misses_are_not_storage_operations()
    {
        var map = await MapAsync(Controller(
            Container + " Eget _own = new(); class Eget { public Task<bool> ExistsAsync() => Task.FromResult(true); public Task DeleteIfExistsAsync() => Task.CompletedTask; }",
            """
            var blob = _c.GetBlobClient("a");          // att hämta en klient är inte att nå lagringen
            _ = blob.GenerateSasUri();                 // en adress räknas ut lokalt
            await _c.AcquireLeaseAsync();              // finns inte i tabellen
            await _own.ExistsAsync();                  // en annan klass som heter likadant
            await _own.DeleteIfExistsAsync();
            """));
        Assert.That(Uses(map.EntryPoints.Single()), Is.Empty);
    }

    [Test]
    public async Task A_blob_container_that_arrives_as_a_parameter_is_just_Blob()
    {
        var map = await MapAsync(Controller("Azure.Storage.Blobs.BlobContainerClient _c = null!;", "await _c.GetBlobClient(\"a\").ExistsAsync();"));
        Assert.That(Uses(map.EntryPoints.Single()), Is.EqualTo(new[] { "storage:Blob | finns" }));
    }

    [TestCase("System.IO.File.Exists(\"a.txt\");", "file | finns a.txt", TestName = "File.Exists")]
    [TestCase("System.IO.File.Exists(Environment.GetEnvironmentVariable(\"F\")!);", "file | finns en fil", TestName = "File.Exists med okänd sökväg")]
    [TestCase("System.IO.File.Delete(\"a.txt\");", "file | ta bort a.txt", TestName = "File.Delete")]
    [TestCase("System.IO.File.Move(\"a.txt\", \"b.txt\");", "file | flytta eller kopiera a.txt", TestName = "File.Move")]
    [TestCase("System.IO.File.Copy(\"a.txt\", \"b.txt\");", "file | flytta eller kopiera a.txt", TestName = "File.Copy")]
    [TestCase("System.IO.File.GetLastWriteTimeUtc(\"a.txt\");", "file | läsa egenskaper a.txt", TestName = "File.GetLastWriteTimeUtc")]
    [TestCase("System.IO.File.SetAttributes(\"a.txt\", System.IO.FileAttributes.Normal);", "file | ändra egenskaper a.txt", TestName = "File.SetAttributes")]
    [TestCase("System.IO.File.ReadAllText(\"a.txt\");", "file | läsa a.txt", TestName = "File.ReadAllText (som förut)")]
    [TestCase("System.IO.Directory.Exists(\"data\");", "file | finns data", TestName = "Directory.Exists")]
    [TestCase("System.IO.Directory.CreateDirectory(\"data\");", "file | skapa data", TestName = "Directory.CreateDirectory")]
    [TestCase("System.IO.Directory.Delete(\"data\", true);", "file | ta bort data", TestName = "Directory.Delete")]
    [TestCase("System.IO.Directory.EnumerateFiles(\"data\").ToList();", "file | lista data", TestName = "Directory.EnumerateFiles")]
    [TestCase("System.IO.Directory.GetFiles(\"data\");", "file | lista data", TestName = "Directory.GetFiles")]
    [TestCase("System.IO.Directory.GetDirectories(\"data\");", "file | lista data", TestName = "Directory.GetDirectories")]
    [TestCase("System.IO.Directory.Move(\"data\", \"old\");", "file | flytta eller kopiera data", TestName = "Directory.Move")]
    [TestCase("System.IO.Directory.Delete(Environment.GetEnvironmentVariable(\"D\")!);", "file | ta bort en mapp", TestName = "Directory.Delete med okänd sökväg")]
    [TestCase("new System.IO.FileInfo(\"a.txt\").Delete();", "file | ta bort a.txt", TestName = "FileInfo.Delete")]
    [TestCase("new System.IO.FileInfo(\"a.txt\").CopyTo(\"b.txt\");", "file | flytta eller kopiera a.txt", TestName = "FileInfo.CopyTo")]
    [TestCase("new System.IO.FileInfo(\"a.txt\").OpenRead();", "file | läsa a.txt", TestName = "FileInfo.OpenRead")]
    [TestCase("var info = new System.IO.FileInfo(\"a.txt\"); if (info.Exists) { }", "file | finns a.txt", TestName = "FileInfo.Exists")]
    [TestCase("var size = new System.IO.FileInfo(\"a.txt\").Length;", "file | läsa egenskaper a.txt", TestName = "FileInfo.Length")]
    [TestCase("new System.IO.DirectoryInfo(\"data\").Delete(true);", "file | ta bort data", TestName = "DirectoryInfo.Delete")]
    [TestCase("var any = new System.IO.DirectoryInfo(\"data\").Exists;", "file | finns data", TestName = "DirectoryInfo.Exists")]
    [TestCase("new System.IO.DirectoryInfo(\"data\").GetFiles();", "file | lista data", TestName = "DirectoryInfo.GetFiles")]
    [TestCase("new System.IO.DirectoryInfo(\"data\").EnumerateDirectories().ToList();", "file | lista data", TestName = "DirectoryInfo.EnumerateDirectories")]
    [TestCase("new System.IO.FileStream(\"a.bin\", System.IO.FileMode.Open);", "file | öppna a.bin", TestName = "new FileStream (som förut)")]
    public async Task File_system_operations_are_named_after_what_they_do(string body, string expected)
    {
        var map = await MapAsync(Controller("", body));
        Assert.That(Uses(map.EntryPoints.Single()), Is.EqualTo(new[] { expected }));
    }

    [Test]
    public async Task File_system_near_misses_do_not_touch_the_disk()
    {
        var map = await MapAsync(Controller("", """
            var combined = System.IO.Path.Combine("a", "b");              // bara text
            var parent = System.IO.Directory.GetParent("a/b");            // bara text
            var here = System.IO.Directory.GetCurrentDirectory();         // processen, inte lagring
            var info = new System.IO.FileInfo("a.txt");                   // att skapa objektet läser inte disken
            var name = info.Name + info.Extension + info.FullName;        // egenskaper som räknas ut ur sökvägen
            var own = Egen.Exists("a.txt") && Egen.Delete("a.txt");       // en annan klass som heter likadant
            """).Replace("public class Kund", "public static class Egen { public static bool Exists(string p) => true; public static bool Delete(string p) => true; }\npublic class Kund"));
        Assert.That(Uses(map.EntryPoints.Single()), Is.Empty);
    }
}
