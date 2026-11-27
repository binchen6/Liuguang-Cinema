import { handleRelayRequest } from "./relay-core.mjs";

export default {
  async fetch(request, env) {
    return handleRelayRequest(request, env, { requireNativeBindings: true });
  },
};
