import { deviceFilter, isLightingDevice, saveStartupMode, setColors } from './rival.mjs';

const storageKey = 'rival-3-rgb.web.v1';
const defaults = ['#ff0000', '#00ff00', '#0000ff'].map(color => ({ color, on: true }));
const find = id => document.getElementById(id);
const connect = find('connect');
const controls = find('controls');
const status = find('status');
const presets = find('presets');
const startup = find('startup');
const saveStartup = find('save-startup');
const hid = navigator.hid;
let device = null;
let busy = false;
let reconnectNeeded = false;
let selectedPreset = '';
const preferences = loadPreferences();

function isPalette(zones) {
  return Array.isArray(zones) && zones.length === 3 && zones.every(zone =>
    typeof zone?.color === 'string' && /^#[0-9a-f]{6}$/i.test(zone.color) && typeof zone.on === 'boolean');
}

function loadPreferences() {
  try {
    const stored = JSON.parse(localStorage.getItem(storageKey));
    return {
      palette: isPalette(stored?.palette) ? stored.palette : null,
      presets: Array.isArray(stored?.presets) ? stored.presets.filter(isPalette) : [],
    };
  } catch {
    return { palette: null, presets: [] };
  }
}

function persist() {
  try {
    localStorage.setItem(storageKey, JSON.stringify(preferences));
    return true;
  } catch {
    return false;
  }
}

function tell(message, error = false) {
  status.textContent = message;
  status.classList.toggle('error', error);
}

function makeZone(name, index) {
  const row = document.createElement('div');
  row.className = 'zone';
  row.innerHTML = `<span class="zone-title">${name}</span>
    <div class="zone-tools">
      <label class="swatch">
        <span class="chip" aria-hidden="true"></span>
        <output aria-hidden="true"></output>
        <input type="color" aria-label="Choose ${name.toLowerCase()} zone color">
      </label>
      <button type="button"></button>
    </div>`;
  const input = row.querySelector('input');
  const toggle = row.querySelector('button');
  input.addEventListener('change', () => changeZones(index, { color: input.value, on: true }));
  toggle.addEventListener('click', () => {
    const zone = (preferences.palette ?? defaults)[index];
    changeZones(index, { ...zone, on: !zone.on });
  });
  find('zones').append(row);
  return { name, input, toggle, chip: row.querySelector('.chip'), output: row.querySelector('output') };
}

const rows = ['Top', 'Middle', 'Bottom'].map(makeZone);

function render() {
  const connected = device?.opened ?? false;
  const palette = preferences.palette ?? defaults;
  find('connection-label').textContent = connected ? 'Mouse connected' : 'Disconnected';
  find('indicator').classList.toggle('online', connected);
  connect.disabled = !hid || busy || connected;
  controls.disabled = !connected || busy;
  saveStartup.disabled = startup.value === '';
  presets.disabled = preferences.presets.length === 0;
  find('save-preset').disabled = preferences.palette === null;

  for (const [index, row] of rows.entries()) {
    const zone = palette[index];
    row.input.value = zone.color;
    row.chip.style.backgroundColor = zone.on ? zone.color : '#0b0e17';
    row.output.textContent = zone.on ? zone.color.toUpperCase() : 'Off';
    row.toggle.textContent = zone.on ? 'Turn off' : 'Turn on';
    row.toggle.setAttribute('aria-label', `${row.toggle.textContent} ${row.name.toLowerCase()} zone`);
  }

  presets.replaceChildren(new Option('Choose a preset', ''),
    ...preferences.presets.map((_, index) => new Option(String(index + 1), String(index + 1))));
  presets.value = selectedPreset;
}

async function run(operation) {
  if (busy) return;
  busy = true;
  render();
  try {
    await operation();
  } catch (error) {
    tell(error.message || 'The mouse command failed.', true);
  } finally {
    busy = false;
    render();
    if (reconnectNeeded) {
      reconnectNeeded = false;
      reconnect();
    }
  }
}

async function openMouse(target, retry = false) {
  for (let attempt = 0; ; attempt++) {
    try {
      if (!target.opened) await target.open();
      device = target;
      if (preferences.palette) {
        await setColors(target, preferences.palette);
        tell('Saved lighting restored.');
      } else {
        tell('Mouse connected. Choose a color or turn the lights off.');
      }
      return;
    } catch (error) {
      if (!retry || attempt === 2) {
        if (!target.opened) {
          device = null;
          throw new Error('Could not open the mouse. Close other RGB apps and reconnect.');
        }
        throw error;
      }
      await new Promise(resolve => setTimeout(resolve, 350));
    }
  }
}

function reconnect() {
  if (busy) {
    reconnectNeeded = true;
    return;
  }
  if (device?.opened) return;
  run(async () => {
    const targets = (await hid.getDevices()).filter(isLightingDevice);
    if (targets.length === 1) await openMouse(targets[0], true);
  });
}

function applyPalette(palette, message, preset = '') {
  return run(async () => {
    await setColors(device, palette);
    preferences.palette = palette;
    selectedPreset = preset;
    const saved = persist();
    tell(saved ? message : 'Lighting applied. This browser did not allow saving it.', !saved);
  });
}

function changeZones(index, update) {
  if (update.color === '#000000') update = { on: false };
  const palette = (preferences.palette ?? defaults).map((zone, number) =>
    index === -1 || index === number ? { ...zone, ...update } : zone);
  applyPalette(palette, 'Lighting applied.');
}

connect.addEventListener('click', () => run(async () => {
  const targets = (await hid.requestDevice({ filters: [deviceFilter] })).filter(isLightingDevice);
  if (targets.length !== 1) {
    tell('Select one wired Rival 3 Gen 2 in the browser prompt.');
    return;
  }
  await openMouse(targets[0]);
}));
find('set-all').addEventListener('click', () => changeZones(-1, { color: find('all-colors').value, on: true }));
find('off').addEventListener('click', () => changeZones(-1, { on: false }));
find('save-preset').addEventListener('click', () => {
  preferences.presets.push(preferences.palette.map(zone => ({ ...zone })));
  selectedPreset = String(preferences.presets.length);
  const saved = persist();
  tell(saved ? `Preset ${selectedPreset} saved.` : `Preset ${selectedPreset} is available until you close this page.`, !saved);
  render();
});
presets.addEventListener('change', () => {
  const number = presets.value;
  if (number !== '') applyPalette(preferences.presets[Number(number) - 1], `Preset ${number} applied.`, number);
});
startup.addEventListener('change', render);
saveStartup.addEventListener('click', () => run(async () => {
  const mode = Number(startup.value);
  await saveStartupMode(device, mode);
  try {
    if (preferences.palette) await setColors(device, preferences.palette);
    tell('Startup lighting saved to the mouse.');
  } catch {
    tell('Startup lighting saved. Reconnect to restore your current colors.', true);
  }
}));

render();
if (!hid) {
  tell('WebHID is unavailable here. Open this page directly in Chrome or Edge. Your browser may restrict USB access.', true);
} else {
  hid.addEventListener('connect', event => { if (isLightingDevice(event.device)) reconnect(); });
  hid.addEventListener('disconnect', event => {
    if (device === event.device) {
      device = null;
      render();
      tell('Mouse disconnected. Waiting for it to reconnect.');
    }
  });
  reconnect();
}
