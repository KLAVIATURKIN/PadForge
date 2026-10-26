// Adapter for ForgePlay's executor to PadForge's existing controller view.
export const controllerView = {
    setStatus(text) { window.PadForgeController?.status(text, true); }
};
