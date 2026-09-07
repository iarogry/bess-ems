// Package runtime is the simulator's composition seam: it walks the
// telemetry sequence in a scenario fixture and pushes each snapshot to
// the Modbus server and the MQTT publisher in lockstep with the
// fixture's offset_millis. plan-RM-M1-simulator.md §136 requires
// deterministic replay; sleeping is delegated to an injected Sleeper so
// unit tests do not block on wall-clock time.
package runtime

import (
	"context"
	"fmt"
	"time"

	"github.com/pt9912/bess-ems/simulators/bess-field-sim/internal/model"
)

// ModbusApplier is implemented by the Modbus server. The interface
// avoids importing the modbus package here so depguard does not have to
// allow runtime → modbus + mqtt + model concurrently.
type ModbusApplier interface {
	Apply(snap model.TelemetrySnapshot)
}

// MqttPublisher is implemented by the MQTT publisher.
type MqttPublisher interface {
	PublishSnapshot(ctx context.Context, snap model.TelemetrySnapshot) error
}

// Sleeper waits the requested duration unless ctx is cancelled. Tests
// inject a NoSleep variant for determinism; production wires
// SleepWithContext.
type Sleeper func(ctx context.Context, d time.Duration) error

// SleepWithContext is the production Sleeper.
func SleepWithContext(ctx context.Context, d time.Duration) error {
	if d <= 0 {
		return nil
	}
	timer := time.NewTimer(d)
	defer timer.Stop()
	select {
	case <-ctx.Done():
		return ctx.Err()
	case <-timer.C:
		return nil
	}
}

// NoSleep returns immediately. Useful in tests that exercise iteration
// logic without wall-clock delay.
func NoSleep(_ context.Context, _ time.Duration) error {
	return nil
}

// Options controls replay behavior that is useful for long-running local
// deployments while keeping deterministic one-shot replay as the default.
type Options struct {
	MqttHeartbeatInterval time.Duration
}

// Orchestrator wires modbus + mqtt + sleeper for one scenario run.
type Orchestrator struct {
	modbus  ModbusApplier
	mqtt    MqttPublisher
	sleeper Sleeper
	options Options
}

// NewOrchestrator constructs an Orchestrator.
func NewOrchestrator(modbus ModbusApplier, mqtt MqttPublisher, sleeper Sleeper) *Orchestrator {
	return NewOrchestratorWithOptions(modbus, mqtt, sleeper, Options{})
}

// NewOrchestratorWithOptions constructs an Orchestrator with explicit replay
// options.
func NewOrchestratorWithOptions(modbus ModbusApplier, mqtt MqttPublisher, sleeper Sleeper, options Options) *Orchestrator {
	if sleeper == nil {
		sleeper = SleepWithContext
	}
	return &Orchestrator{modbus: modbus, mqtt: mqtt, sleeper: sleeper, options: options}
}

// Run walks scn.Telemetry, sleeping between snapshots according to
// strictly-increasing OffsetMillis values. Modbus.Apply is always
// called first, then mqtt.PublishSnapshot. Any publisher error
// short-circuits the run; ctx cancellation interrupts the next sleep.
func (o *Orchestrator) Run(ctx context.Context, scn model.Scenario) error {
	for i, snap := range scn.Telemetry {
		if o.modbus != nil {
			o.modbus.Apply(snap)
		}
		if o.mqtt != nil {
			if err := o.mqtt.PublishSnapshot(ctx, snap); err != nil {
				return fmt.Errorf("publish snapshot %d: %w", i, err)
			}
		}

		if i+1 < len(scn.Telemetry) {
			nextDelta := time.Duration(scn.Telemetry[i+1].OffsetMillis-snap.OffsetMillis) * time.Millisecond
			if err := o.waitUntilNextTick(ctx, i, snap, nextDelta); err != nil {
				return err
			}
		}
	}
	return nil
}

func (o *Orchestrator) waitUntilNextTick(ctx context.Context, index int, snap model.TelemetrySnapshot, delta time.Duration) error {
	if delta <= 0 {
		return nil
	}
	if o.mqtt == nil || o.options.MqttHeartbeatInterval <= 0 {
		return o.sleeper(ctx, delta)
	}
	remaining := delta
	for remaining > 0 {
		sleepFor := remaining
		if sleepFor > o.options.MqttHeartbeatInterval {
			sleepFor = o.options.MqttHeartbeatInterval
		}
		if err := o.sleeper(ctx, sleepFor); err != nil {
			return err
		}
		remaining -= sleepFor
		if remaining <= 0 {
			return nil
		}
		if err := o.mqtt.PublishSnapshot(ctx, snap); err != nil {
			return fmt.Errorf("publish heartbeat snapshot %d: %w", index, err)
		}
	}
	return nil
}
