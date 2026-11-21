const dateTimeFormatter = new Intl.DateTimeFormat(undefined, {
  dateStyle: "medium",
  timeStyle: "short",
});

const timeFormatter = new Intl.DateTimeFormat(undefined, {
  timeStyle: "medium",
});

export function formatDateTime(value: string): string {
  const date = new Date(value);

  return Number.isNaN(date.getTime()) ? value : dateTimeFormatter.format(date);
}

export function formatTime(value: string): string {
  const date = new Date(value);

  return Number.isNaN(date.getTime()) ? value : timeFormatter.format(date);
}

export function formatRelative(value: string | null): string {
  if (!value) {
    return "never";
  }

  const date = new Date(value).getTime();

  if (Number.isNaN(date)) {
    return "unknown";
  }

  const seconds = Math.max(0, Math.round((Date.now() - date) / 1000));

  if (seconds < 10) {
    return "just now";
  }

  if (seconds < 60) {
    return `${seconds}s ago`;
  }

  const minutes = Math.floor(seconds / 60);

  if (minutes < 60) {
    return `${minutes}m ago`;
  }

  const hours = Math.floor(minutes / 60);

  if (hours < 24) {
    return `${hours}h ago`;
  }

  const days = Math.floor(hours / 24);

  return `${days}d ago`;
}

export function formatConfidence(confidence: number | null): string | null {
  if (confidence === null || Number.isNaN(confidence)) {
    return null;
  }

  const percent = confidence <= 1 ? confidence * 100 : confidence;

  return `${Math.round(percent)}%`;
}
