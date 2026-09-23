# Tools

* `phd2-parity/` — compares star detection with PHD2's compiled `star.cpp` (see its README).
* `phd2-algo-golden/` — generates golden sequences from PHD2's guide algorithm sources.
* `e2e/` — end-to-end harness: builds a PINS image with the plugin (from a pins checkout that has this repo at
  `NINA.Plugins/pins-guider`), runs it with bridge networking on remapped ports and drives ninaAPI through the
  built-in simulator, the INDI camera/telescope simulators (with a test-only GSC layer), camera ST4 and
  robustness scenarios. `./run-e2e.sh --build`. Paths in the scripts point at the author's workspace.
* `smoke/` — smoke test of a full image (TNS native guider REST API, WebSocket, UI screenshots through
  headless Chrome/DevTools). Browse the container through its bridge IP so the app's ninaAPI port discovery
  (1888) reaches the test container and not a PINS instance on the host.
