import { createDesktopDataService } from './desktopDataService'
import { createMetadataService } from '../metadataService'

type DataServiceOptions = Record<string, any> & {
  metadataService?: unknown
}

export function createDataService(options: DataServiceOptions = {}) {
  const metadataService = options.metadataService ?? createMetadataService()

  return createDesktopDataService({
    ...options,
    metadataService,
  })
}
