import { dotnet } from "./_framework/dotnet.js";

function fail(error) {
  try {
    postMessage({
      type: "error",
      error: String(error?.stack || error || "Unknown worker error")
    });
  } catch {}
}

try {
  const { runMain, getConfig, setModuleImports } =
    await dotnet.withDiagnosticTracing(false).create();

  setModuleImports("texture-view", {
    render(width, height, encoded, path) {
      if (
        !Number.isInteger(width) ||
        !Number.isInteger(height) ||
        width <= 0 ||
        height <= 0 ||
        width * height > 65536
      ) {
        throw new Error("Texture pixel budget exceeded");
      }

      const raw = Uint8Array.from(
        atob(encoded),
        character => character.charCodeAt(0)
      );

      if (raw.byteLength !== width * height * 4) {
        throw new Error("RGBA byte count mismatch");
      }

      const buffer = raw.buffer;

      postMessage(
        {
          type: "pixels",
          path,
          width,
          height,
          pixels: buffer
        },
        [buffer]
      );
    }
  });

  const test =
    new URL(globalThis.location.href)
      .searchParams
      .get("test");

  const args =
    test === "live-buildpatch"
      ? [
          "--live-buildpatch-base=" +
          new URL("./live/", globalThis.location.href).toString()
        ]
      : [];

  const exitCode =
    await runMain(
      getConfig().mainAssemblyName,
      args
    );

  postMessage({
    type: "done",
    exitCode
  });
} catch (error) {
  fail(error);
}
