// Protocol references are listed in the repository README.
export const deviceFilter = {
  vendorId: 0x1038, productId: 0x1870, usagePage: 0xffc0, usage: 1,
};

export function isLightingDevice(device) {
  return device.vendorId === deviceFilter.vendorId && device.productId === deviceFilter.productId &&
    device.collections.some(collection => collection.usagePage === deviceFilter.usagePage &&
      collection.usage === deviceFilter.usage && collection.outputReports.some(report => report.reportId === 0));
}

function report(command, ...args) {
  // WebHID takes the report ID separately from the 64-byte USB payload.
  const bytes = new Uint8Array(64);
  bytes.set([command, ...args]);
  return bytes;
}

export function modeReport(mode) {
  return report(0x27, (mode >> 1) & 1, mode & 1);
}

export function colorReport(zones) {
  const colors = zones.flatMap(zone => zone.on ? zone.color.slice(1).match(/../g).map(hex => parseInt(hex, 16)) : [0, 0, 0]);
  return report(0x21, 0x07, ...colors);
}

async function send(device, bytes) {
  let timer;
  let onReport;
  const acknowledged = new Promise((resolve, reject) => {
    onReport = event => {
      if (event.reportId === 0 && event.data.byteLength && event.data.getUint8(0) === bytes[0]) resolve();
    };
    device.addEventListener('inputreport', onReport);
    timer = setTimeout(() => reject(new Error('The mouse did not acknowledge the lighting command.')), 1000);
  });

  try {
    await Promise.all([Promise.resolve().then(() => device.sendReport(0, bytes)), acknowledged]);
  } finally {
    clearTimeout(timer);
    device.removeEventListener('inputreport', onReport);
  }
}

export async function setColors(device, zones) {
  await send(device, modeReport(0));
  await send(device, colorReport(zones));
}

export async function saveStartupMode(device, mode) {
  await send(device, modeReport(mode));
  await send(device, report(0x11, 0x00));
}
