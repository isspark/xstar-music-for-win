/* Frame-rate aware audio feature store fed by the C# audio messages. */

export const audio = {
  spectrum: new Float32Array(64),
  bass: 0,
  bassTarget: 0,
  mid: 0,
  midTarget: 0,
  treble: 0,
  trebleTarget: 0,
  beat: 0,
  level: 0,
  // shaped / boosted values used for the stronger visual pumping
  pulse: 0,
  energy: 0,
  hasData: false,
};

export function feedAudio(msg) {
  const spectrum = msg.spectrum;
  if (spectrum && spectrum.length) {
    const n = Math.min(spectrum.length, audio.spectrum.length);
    for (let i = 0; i < n; i++) audio.spectrum[i] = spectrum[i] || 0;
    audio.hasData = true;
  }
  if (typeof msg.bass === "number") audio.bassTarget = Math.max(audio.bassTarget, msg.bass);
  if (typeof msg.mid === "number") audio.midTarget = Math.max(audio.midTarget, msg.mid);
  if (typeof msg.treble === "number") audio.trebleTarget = Math.max(audio.trebleTarget, msg.treble);
  if (msg.beat) audio.beat = 1;
}

export function updateAudio(dt) {
  const attack = Math.min(1, dt * 20);
  const releaseDecay = Math.pow(0.02, dt);

  audio.bass += (audio.bassTarget - audio.bass) * attack;
  audio.mid += (audio.midTarget - audio.mid) * attack;
  audio.treble += (audio.trebleTarget - audio.treble) * attack;

  audio.bassTarget *= releaseDecay;
  audio.midTarget *= releaseDecay;
  audio.trebleTarget *= releaseDecay;
  audio.beat *= Math.pow(0.02, dt);

  audio.level = audio.bass * 0.6 + audio.mid * 0.3 + audio.treble * 0.1;

  // Bass is usually well below full scale; curve + gain it so the motion reads clearly.
  audio.pulse = Math.min(1.7, Math.pow(Math.min(1, audio.bass * 1.7), 0.72));
  audio.energy = Math.min(1.7, audio.bass * 0.7 + audio.mid * 0.5 + audio.treble * 0.4);
}
