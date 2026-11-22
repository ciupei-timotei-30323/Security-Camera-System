import { existsSync } from "node:fs";
import { readFile } from "node:fs/promises";
import { loadEnvFile } from "node:process";
import { fileURLToPath } from "node:url";
import { neon } from "@neondatabase/serverless";
import { hash } from "bcryptjs";

// Load `.env` from the service root when it exists. Real environment variables always win
// because loadEnvFile does not overwrite variables that are already set.
const envPath = fileURLToPath(new URL("../.env", import.meta.url));
if (existsSync(envPath)) {
  loadEnvFile(envPath);
}

const databaseUrl = process.env.DATABASE_URL;
if (!databaseUrl) {
  console.error("DATABASE_URL is required. Add it to external-auth-service/.env or export it.");
  process.exit(1);
}

type SeedKit = {
  systemId: string;
  username: string;
  password: string;
  piApiKey: string;
};

const kits: SeedKit[] = [
  {
    systemId: "sys-001",
    username: "cam_admin_1",
    password: process.env.SEED_CAM_ADMIN_1_PASSWORD ?? "factory-pass-sys-001",
    piApiKey: process.env.SEED_PI_API_KEY_SYS_001 ?? "dev-pi-key-sys-001"
  },
  {
    systemId: "sys-002",
    username: "cam_admin_2",
    password: process.env.SEED_CAM_ADMIN_2_PASSWORD ?? "factory-pass-sys-002",
    piApiKey: process.env.SEED_PI_API_KEY_SYS_002 ?? "dev-pi-key-sys-002"
  }
];

const sql = neon(databaseUrl);
const schemaPath = fileURLToPath(new URL("../schema.sql", import.meta.url));
const schema = await readFile(schemaPath, "utf8");

await sql.query(schema);

for (const kit of kits) {
  const passwordHash = await hash(kit.password, 12);

  await sql`
    INSERT INTO factory_kits (system_id, admin_username, password_hash, pi_api_key)
    VALUES (${kit.systemId}, ${kit.username}, ${passwordHash}, ${kit.piApiKey})
    ON CONFLICT (system_id) DO UPDATE SET
      admin_username = EXCLUDED.admin_username,
      password_hash = EXCLUDED.password_hash,
      pi_api_key = EXCLUDED.pi_api_key,
      is_active = TRUE
  `;

  console.log(`Seeded ${kit.username} -> ${kit.systemId}`);
}

console.log("factory_kits schema and sample kits are ready.");
