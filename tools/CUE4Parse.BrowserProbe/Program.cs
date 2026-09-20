using CUE4Parse.UE4.IO;
using CUE4Parse.UE4.IO.Objects;

Console.WriteLine("NovaSparx CUE4Parse browser probe");

if (!OperatingSystem.IsBrowser())
{
    Console.Error.WriteLine("Probe is not running on the browser-wasm runtime.");
    return 2;
}

Console.WriteLine(typeof(IoStoreReader).Assembly.FullName);
Console.WriteLine(typeof(IoStoreReader).FullName);
Console.WriteLine(typeof(FIoStoreTocResource).FullName);
Console.WriteLine("CUE4PARSE_BROWSER_WASM_OK");

return 0;
