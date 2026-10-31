/** Prepare a cover's backdrop before changing to it, and discard late track replies. */
export function createArtworkBackdropController<T>(
  load: (source: string) => Promise<T>,
  apply: (loaded: T | null) => void,
) {
  let generation = 0

  return {
    update(source: string | null) {
      const current = ++generation
      if (!source) {
        apply(null)
        return
      }

      void load(source).then(
        (loaded) => { if (current === generation) apply(loaded) },
        () => { if (current === generation) apply(null) },
      )
    },
    cancel() { generation++ },
  }
}
