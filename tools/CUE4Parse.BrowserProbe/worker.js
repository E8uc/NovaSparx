import { dotnet } from "./_framework/dotnet.js";

const relayAllowedHosts =
  new Set([
    "egdownload.fastly-edge.com",
    "download.epicgames.com",
    "fortnite-direct.dillycdn.com",
    "stormforge.dillycdn.com"
  ]);

function installRangeRelayFetch(relayEndpoint) {
  if (!relayEndpoint) return () => {};

  const nativeFetch =
    globalThis.fetch.bind(globalThis);

  const relayUrl =
    new URL(relayEndpoint, globalThis.location.href);

  const maxRangeBytes =
    4 * 1024 * 1024;

  const maxFileBytes =
    64 * 1024 * 1024;

  function requestUrl(input) {
    if (typeof input === "string") return input;
    if (input instanceof URL) return input.toString();
    return input?.url || "";
  }

  function requestSignal(input, init) {
    return init?.signal || input?.signal || undefined;
  }

  async function relayRange(target, start, end, signal) {
    const url =
      new URL(relayUrl);

    url.searchParams.set("url", target.toString());
    url.searchParams.set("start", String(start));
    url.searchParams.set("end", String(end));

    const response =
      await nativeFetch(
        url,
        {
          method: "GET",
          signal,
          cache: "no-store",
          headers: {
            accept:
              "application/octet-stream,*/*;q=0.8"
          }
        }
      );

    if (!response.ok) {
      throw new Error(
        "Relay returned HTTP " +
        response.status
      );
    }

    const bytes =
      new Uint8Array(
        await response.arrayBuffer()
      );

    const expected =
      end - start + 1;

    if (
      bytes.byteLength < 1 ||
      bytes.byteLength > expected
    ) {
      throw new Error(
        "Relay returned an invalid byte window"
      );
    }

    return {
      bytes,
      contentRange:
        response.headers.get(
          "content-range"
        ),
      contentType:
        response.headers.get(
          "content-type"
        ) ||
        "application/octet-stream"
    };
  }

  function totalFromContentRange(value) {
    const match =
      String(value || "")
        .match(
          /^bytes\s+\d+-\d+\/(\d+)$/i
        );

    const total =
      Number(match?.[1] || 0);

    if (
      !Number.isSafeInteger(total) ||
      total < 1 ||
      total > maxFileBytes
    ) {
      throw new Error(
        "Relay did not expose a safe total file size"
      );
    }

    return total;
  }

  async function relayWhole(target, input, init) {
    const signal =
      requestSignal(input, init);

    const first =
      await relayRange(
        target,
        0,
        0,
        signal
      );

    const total =
      totalFromContentRange(
        first.contentRange
      );

    const output =
      new Uint8Array(total);

    output.set(first.bytes, 0);

    let offset =
      first.bytes.byteLength;

    while (offset < total) {
      if (signal?.aborted) {
        throw signal.reason ||
          new DOMException(
            "The operation was aborted.",
            "AbortError"
          );
      }

      const end =
        Math.min(
          total - 1,
          offset +
            maxRangeBytes -
            1
        );

      const part =
        await relayRange(
          target,
          offset,
          end,
          signal
        );

      const expected =
        end - offset + 1;

      if (
        part.bytes.byteLength !==
        expected
      ) {
        throw new Error(
          "Relay returned an incomplete file segment"
        );
      }

      output.set(
        part.bytes,
        offset
      );

      offset +=
        part.bytes.byteLength;
    }

    return new Response(
      output,
      {
        status: 200,
        headers: {
          "content-type":
            first.contentType,
          "content-length":
            String(total),
          "x-novasparx-fetch-source":
            "range-relay"
        }
      }
    );
  }

  globalThis.fetch =
    async (input, init = {}) => {
      const raw =
        requestUrl(input);

      let target;

      try {
        target =
          new URL(
            raw,
            globalThis.location.href
          );
      } catch {
        return nativeFetch(input, init);
      }

      const method =
        String(
          init?.method ||
          input?.method ||
          "GET"
        ).toUpperCase();

      if (
        method !== "GET" ||
        target.origin ===
          globalThis.location.origin ||
        !relayAllowedHosts.has(
          target.hostname.toLowerCase()
        )
      ) {
        return nativeFetch(input, init);
      }

      try {
        return await nativeFetch(
          input,
          init
        );
      } catch (directError) {
        if (
          requestSignal(
            input,
            init
          )?.aborted
        ) {
          throw directError;
        }

        return await relayWhole(
          target,
          input,
          init
        );
      }
    };

  return () => {
    globalThis.fetch =
      nativeFetch;
  };
}

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
        width * height > 2048 * 2048
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

  const workerUrl =
    new URL(
      globalThis.location.href
    );

  const test =
    workerUrl.searchParams
      .get("test");

  let restoreFetch =
    () => {};

  if (
    test ===
      "live-texture-relay" ||
    test ===
      "resolve-texture-relay"
  ) {
    restoreFetch =
      installRangeRelayFetch(
        workerUrl.searchParams
          .get("relay") ||
        "./edge/range"
      );
  }

  const liveBase =
    new URL("./live/", globalThis.location.href).toString();

  const args =
    test === "live-buildpatch"
      ? [
          "--live-buildpatch-base=" +
          liveBase
        ]
      : test === "live-texture"
        ? [
            "--live-texture-base=" +
            liveBase
          ]
        : test ===
            "live-texture-relay"
          ? [
              "--live-texture-base=" +
                liveBase,
              "--live-texture-manifest-url=" +
                String(
                  workerUrl.searchParams
                    .get("manifest") ||
                  ""
                ),
              "--live-texture-chunk-base=" +
                String(
                  workerUrl.searchParams
                    .get("chunkBase") ||
                  ""
                )
            ]
          : test ===
              "resolve-texture-relay"
            ? [
                "--resolve-texture-path=" +
                  String(
                    workerUrl.searchParams
                      .get("path") ||
                    ""
                  ),
                "--resolve-texture-toc=" +
                  String(
                    workerUrl.searchParams
                      .get("toc") ||
                    ""
                  ),
                "--resolve-texture-manifest-url=" +
                  String(
                    workerUrl.searchParams
                      .get("manifest") ||
                    ""
                  ),
                "--resolve-texture-chunk-base=" +
                  String(
                    workerUrl.searchParams
                      .get("chunkBase") ||
                    ""
                  ),
                "--resolve-texture-mappings-api=" +
                  String(
                    workerUrl.searchParams
                      .get("mappingsApi") ||
                    "https://api.fortniteapi.com/v1/mappings"
                  ),
                "--resolve-texture-aes-api=" +
                  String(
                    workerUrl.searchParams
                      .get("aesApi") ||
                    "https://export-service-new.dillyapis.com/v1/aes"
                  ),
                "--resolve-texture-max-size=" +
                  String(
                    workerUrl.searchParams
                      .get("maxSize") ||
                    "1024"
                  )
              ]
            : [];

  const exitCode =
    await runMain(
      getConfig().mainAssemblyName,
      args
    );

  restoreFetch?.();

  postMessage({
    type: "done",
    exitCode
  });
} catch (error) {
  fail(error);
}
