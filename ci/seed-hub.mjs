// Seed devices into the Collector via Hub SaveDevicesAsync (Collector writes its own data dir).
import fs from 'node:fs';
import * as signalR from '@microsoft/signalr';

const src = 'C:/Users/35953/AppData/Roaming/Kanban/Config/devices.json';
const raw = JSON.parse(fs.readFileSync(src, 'utf8'));
if (!Array.isArray(raw) || raw.length === 0) {
  console.log('[abort] source devices.json empty');
  process.exit(1);
}

const dtos = raw.map((d) => ({
  id: d.Id,
  name: d.Name,
  machineType: d.MachineType ?? '',
  okCountAddress: d.OkCountAddress,
  ngCountAddress: d.NgCountAddress,
  statusCountAddress: d.StatusCountAddress,
  productionResetAddress: d.ProductionResetAddress,
  recipeName: d.RecipeName ?? '',
  recipeValue: d.RecipeValue ?? 0,
  recipeAddress: d.RecipeAddress ?? '',
  targetCycle: d.TargetCycle ?? 0,
  alarms: (d.Alarms ?? []).map((a) => ({
    id: a.Id, deviceId: a.DeviceId, name: a.Name, plcAddress: a.PlcAddress,
    description: a.Description ?? '', level: a.Level,
  })),
  defects: (d.Defects ?? []).map((x) => ({
    id: x.Id, deviceId: x.DeviceId, name: x.Name, plcAddress: x.PlcAddress,
    severity: x.Severity, category: x.Category,
  })),
  countAlarms: (d.CountAlarms ?? []).map((x) => ({
    id: x.Id, deviceId: x.DeviceId, name: x.Name, plcAddress: x.PlcAddress,
    maxValue: x.MaxValue ?? 0, enabled: x.Enabled ?? false,
    description: x.Description ?? '', unit: x.Unit ?? '',
  })),
}));

console.log('[seeding]', dtos.length, 'devices');
const conn = new signalR.HubConnectionBuilder()
  .withUrl('http://127.0.0.1:5130/hubs/kanban', { transport: signalR.HttpTransportType.WebSockets })
  .configureLogging(signalR.LogLevel.Debug)
  .build();
await conn.start();
await conn.invoke('SaveDevicesAsync', dtos);
console.log('[saved]');
const back = await conn.invoke('GetDevicesAsync');
console.log('[verify]', back?.length, 'devices:', back?.map((d) => `${d.name}(${d.id})`).join(', '));
await conn.stop();
