export type Role = "Admin" | "User";

export interface AuthUser {
  id: number | null;
  username: string;
  role: Role;
  systemId: string;
  createdAtUtc?: string | null;
}

export interface LoginUser {
  id: number | null;
  username: string;
  role: Role;
  systemId: string;
}

export interface LoginResult {
  user: LoginUser;
  expiresAtUtc: string;
}

export interface ProblemDetails {
  type?: string;
  title?: string;
  status?: number;
  detail?: string;
  instance?: string;
  code?: string;
}

export interface DetectionEvent {
  id: number;
  systemId: string;
  timestamp: string;
  isSuspect: boolean;
  confidence: number | null;
  detectedFaceUrl: string | null;
  fullImageUrl: string | null;
  isOfflineSync: boolean;
  clientEventId: string | null;
  annotatedImageUrl: string | null;
}

export interface PagedEvents {
  items: DetectionEvent[];
  total: number;
  page: number;
  pageSize: number;
}

export interface DeviceStatus {
  online: boolean;
  lastSeen: string | null;
}

export interface SystemStatus {
  camera: DeviceStatus;
  alertTerminal: DeviceStatus;
  backend: string;
  cvService: string;
}

export type AwayMode = "off" | "indefinite" | "scheduled";

export interface AwayModeSetting {
  systemId: string;
  mode: AwayMode;
  startTime: string | null;
  endTime: string | null;
  alertEmail: string | null;
  timeZone: string;
  isArmedNow: boolean;
  isArmed: boolean;
  updatedAtUtc: string;
}

export interface UpdateAwayModeRequest {
  mode: AwayMode;
  startTime?: string | null;
  endTime?: string | null;
  alertEmail?: string | null;
  timeZone: string;
}

export interface UserSummary {
  id: number;
  systemId: string;
  username: string;
  role: Role;
  createdAtUtc: string;
}

export interface CreateUserRequest {
  username: string;
  password: string;
  role?: Role;
}
