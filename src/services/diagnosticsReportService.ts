interface UploadDiagnosticsReportOptions {
  consent?: boolean
  reason?: string
  maxEvents?: number
}

interface DiagnosticsUploadResult {
  uploaded?: boolean
  skipped?: boolean
  reason?: string
  eventCount?: number
  status?: number
  [key: string]: unknown
}

export function hasDiagnosticsReportEndpoint(): boolean {
  return false
}

export async function uploadDiagnosticsReport({
  consent,
  reason = 'manual',
  maxEvents = 800,
}: UploadDiagnosticsReportOptions = {}): Promise<DiagnosticsUploadResult> {
  void consent
  void reason
  void maxEvents

  return {
    uploaded: false,
    skipped: true,
    reason: 'community_no_upload_endpoint',
    eventCount: 0,
    status: 0,
  }
}
