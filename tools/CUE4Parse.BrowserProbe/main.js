import { dotnet } from "./_framework/dotnet.js";

// Executed by an actual browser page, never by Node. Node only hosts the files
// and drives Chromium in runtime.test.mjs.
globalThis.cue4parseProbe = { state: "starting", assetParsingProven: false };
try {
  const { runMain, getConfig } = await dotnet.withDiagnosticTracing(false).create();
  const exitCode = await runMain(getConfig().mainAssemblyName, []);
  globalThis.cue4parseProbe = { state: exitCode === 0 ? "ready" : "failed", exitCode, assetParsingProven: false };
} catch (error) {
  globalThis.cue4parseProbe = { state: "failed", error: String(error?.stack || error), assetParsingProven: false };
  console.error(error);
}
