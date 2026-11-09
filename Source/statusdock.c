#include <furi.h>
#include <furi_hal_power.h>
#include <gui/gui.h>
#include <input/input.h>
#include <notification/notification_messages.h>
#include <power/power_service/power.h>
#include <stdio.h>

// The loader's signal and argument carry one complete Windows sample.
#define SAMPLE_V1_PREFIX 0x50440000U
#define SAMPLE_V1_MASK 0xFFFC0000U
#define SAMPLE_V2_PREFIX 0x54000000U
#define SAMPLE_V2_MASK 0xFC000000U
#define SAMPLE_V3_PREFIX 0x60000000U
#define SAMPLE_V3_MASK 0xE0000000U
#define SAMPLE_V3_INVERT_DISPLAY 0x10000000U
#define SAMPLE_V3_LARGE_CLOCK 0x08000000U
#define SAMPLE_V3_HIDE_SECONDS 0x04000000U
// The loader reports one boolean per signal. Acknowledge the saved view in queue order.
#define VIEW_STATS_SIGNAL 0x53544401U
#define VIEW_CLOCK_SIGNAL 0x53544402U
#define VIEW_SAVED_SIGNAL 0x53544403U
#define SECONDS_PER_DAY 86400U
#define QUARTERS_PER_SECOND 4U
#define GPU_UNKNOWN 127U
#define METRIC_UNKNOWN 255U
#define LINK_TIMEOUT_MS 15000U
#define POWER_TIMEOUT_MS 8000U

typedef struct {
    uint32_t clock_quarters;
    uint8_t cpu;
    uint8_t ram;
    uint8_t gpu;
    uint8_t cpu_temp;
    uint8_t gpu_temp;
    bool large_clock;
    bool show_seconds;
    bool invert_display;
} DeskSample;

typedef enum {
    DeskEventSample,
    DeskEventExit,
    DeskEventToggleView,
    DeskEventViewSaved,
} DeskEventKind;

typedef enum {
    DeskViewNone,
    DeskViewStats,
    DeskViewClock,
} DeskPendingView;

typedef struct {
    DeskEventKind kind;
    union {
        DeskSample sample;
        DeskPendingView saved_view;
    };
} DeskEvent;

typedef enum {
    DeskWaiting,
    DeskLive,
    DeskBlank,
} DeskScreen;

typedef struct {
    FuriMessageQueue* events;
    FuriMutex* model_mutex;
    DeskSample sample;
    uint32_t clock_seconds;
    DeskScreen screen;
    DeskPendingView pending_view;
} DeskApp;

static DeskApp app;

// These 5 × 7 digits match the Windows preview. The last glyph is the colon.
static const uint8_t desk_clock_glyphs[][7] = {
    {14, 17, 17, 17, 17, 17, 14},
    {4, 12, 4, 4, 4, 4, 14},
    {14, 17, 1, 6, 8, 16, 31},
    {30, 1, 1, 14, 1, 1, 30},
    {2, 6, 10, 18, 31, 2, 2},
    {31, 16, 16, 30, 1, 1, 30},
    {14, 16, 16, 30, 17, 17, 14},
    {31, 1, 2, 4, 8, 8, 8},
    {14, 17, 17, 14, 17, 17, 14},
    {14, 17, 17, 15, 1, 1, 14},
    {0, 0, 2, 2, 0, 2, 2},
};

static void desk_draw_clock_digits(
    Canvas* canvas, const char* text, int x, int y, int scale_x, int height) {
    for(; *text; ++text) {
        bool colon = *text == ':';
        const uint8_t* glyph = desk_clock_glyphs[colon ? 10 : *text - '0'];
        int width = colon ? 3 : 5;
        for(int row = 0; row < 7; ++row) {
            int top = row * height / 7;
            int row_height = (row + 1) * height / 7 - top;
            for(int column = 0; column < width; ++column) {
                if(glyph[row] & (1U << (width - 1 - column))) {
                    canvas_draw_box(
                        canvas, x + column * scale_x, y + top, scale_x, row_height);
                }
            }
        }
        x += (width + 1) * scale_x;
    }
}

static void desk_draw_clock_group(
    Canvas* canvas,
    const char* clock,
    const char* seconds,
    int clock_scale_x,
    int clock_height,
    int seconds_scale_x,
    int seconds_height,
    int gap,
    int bottom) {
    // Digit columns, colon columns and the gaps between characters.
    int clock_width = (4 * 5 + 3 + 4) * clock_scale_x;
    int seconds_width = (2 * 5 + 3 + 2) * seconds_scale_x;
    int width = clock_width + (*seconds ? gap + seconds_width : 0);
    int left = (128 - width) / 2;
    desk_draw_clock_digits(
        canvas, clock, left, bottom - clock_height, clock_scale_x, clock_height);
    desk_draw_clock_digits(
        canvas,
        seconds,
        left + clock_width + gap,
        bottom - seconds_height,
        seconds_scale_x,
        seconds_height);
}

static void desk_meter(
    Canvas* canvas,
    uint8_t baseline,
    const char* label,
    uint8_t percent,
    uint8_t temp) {
    char text[10];
    canvas_draw_str(canvas, 2, baseline, label);
    canvas_draw_frame(canvas, 26, baseline - 7, 43, 7);
    if(percent <= 100) {
        uint8_t width = (41U * percent + 50U) / 100U;
        if(width) canvas_draw_box(canvas, 27, baseline - 6, width, 5);
        snprintf(text, sizeof(text), "%u%%", (unsigned)percent);
    } else {
        snprintf(text, sizeof(text), "--");
    }
    canvas_draw_str_aligned(canvas, 95, baseline, AlignRight, AlignBottom, text);
    if(temp != METRIC_UNKNOWN) {
        snprintf(text, sizeof(text), "%u C", (unsigned)temp);
        canvas_draw_str_aligned(canvas, 126, baseline, AlignRight, AlignBottom, text);
    }
}

static void desk_draw(Canvas* canvas, void* context) {
    DeskApp* app = context;
    furi_mutex_acquire(app->model_mutex, FuriWaitForever);
    DeskScreen screen = app->screen;
    DeskSample sample = app->sample;
    uint32_t clock_seconds = app->clock_seconds;
    furi_mutex_release(app->model_mutex);

    canvas_clear(canvas);
    if(screen == DeskBlank) return;
    canvas_set_color(canvas, ColorBlack);
    if(screen == DeskWaiting) {
        canvas_set_font(canvas, FontPrimary);
        canvas_draw_str_aligned(canvas, 64, 24, AlignCenter, AlignBottom, "STATUSDOCK");
        canvas_set_font(canvas, FontSecondary);
        canvas_draw_str_aligned(canvas, 64, 42, AlignCenter, AlignBottom, "Waiting for Windows");
        return;
    }

    Color foreground = ColorBlack;
    Color background = ColorWhite;
    if(sample.invert_display) {
        canvas_draw_box(canvas, 0, 0, 128, 64);
        foreground = ColorWhite;
        background = ColorBlack;
    }
    canvas_set_color(canvas, foreground);

    char clock[6];
    snprintf(
        clock,
        sizeof(clock),
        "%02u:%02u",
        (unsigned)(clock_seconds / 3600U % 24U),
        (unsigned)(clock_seconds / 60U % 60U));
    char seconds[4] = "";
    if(sample.show_seconds) {
        snprintf(seconds, sizeof(seconds), ":%02u", (unsigned)(clock_seconds % 60U));
    }
    if(sample.large_clock) {
        if(sample.show_seconds) {
            // A 21-pixel clock and 16-pixel seconds share a centered baseline.
            desk_draw_clock_group(canvas, clock, seconds, 3, 21, 2, 16, 6, 42);
        } else {
            desk_draw_clock_group(canvas, clock, seconds, 4, 28, 1, 7, 4, 46);
        }
        return;
    }

    canvas_draw_box(canvas, 0, 0, 128, 23);
    canvas_set_color(canvas, background);
    desk_draw_clock_group(canvas, clock, seconds, 3, 21, 2, 14, 6, 22);

    canvas_set_color(canvas, foreground);
    canvas_set_font(canvas, FontSecondary);
    desk_meter(canvas, 35, "CPU", sample.cpu, sample.cpu_temp);
    desk_meter(canvas, 48, "RAM", sample.ram, METRIC_UNKNOWN);
    desk_meter(canvas, 61, "GPU", sample.gpu, sample.gpu_temp);
}

static void desk_input(InputEvent* input, void* context) {
    DeskApp* app = context;
    if(input->key == InputKeyBack && input->type == InputTypePress) {
        const DeskEvent event = {.kind = DeskEventExit};
        furi_message_queue_put(app->events, &event, 0);
    } else if(input->key == InputKeyOk && input->type == InputTypeShort) {
        const DeskEvent event = {.kind = DeskEventToggleView};
        furi_message_queue_put(app->events, &event, 0);
    }
}

static bool desk_signal(uint32_t signal, void* arg, void* context) {
    UNUSED(context);
    if(signal == VIEW_STATS_SIGNAL || signal == VIEW_CLOCK_SIGNAL) {
        FURI_CRITICAL_ENTER();
        DeskPendingView requested = signal == VIEW_STATS_SIGNAL ? DeskViewStats : DeskViewClock;
        bool handled = app.events && app.pending_view == requested;
        FURI_CRITICAL_EXIT();
        return handled;
    }

    DeskEvent event = {0};
    bool v3 = (signal & SAMPLE_V3_MASK) == SAMPLE_V3_PREFIX;
    bool quarter_clock = v3 || (signal & SAMPLE_V2_MASK) == SAMPLE_V2_PREFIX;
    if(signal == FuriSignalExit) {
        event.kind = DeskEventExit;
    } else if(signal == VIEW_SAVED_SIGNAL && (uintptr_t)arg <= 1U) {
        event.kind = DeskEventViewSaved;
        event.saved_view = arg ? DeskViewClock : DeskViewStats;
    } else if(quarter_clock || (signal & SAMPLE_V1_MASK) == SAMPLE_V1_PREFIX) {
        uint32_t packed = (uintptr_t)arg;
        event.sample = (DeskSample){
            .clock_quarters = quarter_clock ? signal & 0x7FFFFU :
                                             (signal & 0x7FFU) * 60U * QUARTERS_PER_SECOND,
            .gpu = (signal >> (quarter_clock ? 19 : 11)) & 0x7FU,
            .cpu = packed >> 24,
            .ram = (packed >> 16) & 0xFFU,
            .cpu_temp = (packed >> 8) & 0xFFU,
            .gpu_temp = packed & 0xFFU,
            .large_clock = v3 && (signal & SAMPLE_V3_LARGE_CLOCK),
            .show_seconds = quarter_clock && !(v3 && (signal & SAMPLE_V3_HIDE_SECONDS)),
            .invert_display = v3 && (signal & SAMPLE_V3_INVERT_DISPLAY),
        };
        if(event.sample.clock_quarters >= SECONDS_PER_DAY * QUARTERS_PER_SECOND ||
           (event.sample.gpu > 100 && event.sample.gpu != GPU_UNKNOWN) ||
           (event.sample.cpu > 100 && event.sample.cpu != METRIC_UNKNOWN) ||
           event.sample.ram > 100) {
            return false;
        }
    } else {
        return false;
    }
    FURI_CRITICAL_ENTER();
    bool queued = app.events && furi_message_queue_put(app.events, &event, 0) == FuriStatusOk;
    FURI_CRITICAL_EXIT();
    return queued;
}

int32_t statusdock_app(void* args) {
    UNUSED(args);
    app = (DeskApp){
        .events = furi_message_queue_alloc(8, sizeof(DeskEvent)),
        .model_mutex = furi_mutex_alloc(FuriMutexTypeNormal),
        .screen = DeskWaiting,
    };
    Gui* gui = furi_record_open(RECORD_GUI);
    NotificationApp* notifications = furi_record_open(RECORD_NOTIFICATION);
    ViewPort* view = view_port_alloc();
    view_port_draw_callback_set(view, desk_draw, &app);
    view_port_input_callback_set(view, desk_input, &app);
    gui_add_view_port(gui, view, GuiLayerFullscreen);
    FuriThread* thread = furi_thread_get_current();
    furi_thread_set_signal_callback(thread, desk_signal, NULL);

    uint32_t now = furi_get_tick();
    uint32_t last_frame = now;
    uint32_t last_power = now;
    uint32_t last_poll = now;
    bool saw_frame = false;
    bool saw_power = furi_hal_power_get_usb_voltage() >= 4.0f;
    bool power_off_requested = false;
    const uint32_t quarter_ticks = furi_ms_to_ticks(250);
    while(true) {
        DeskEvent event;
        bool received = furi_message_queue_get(app.events, &event, furi_ms_to_ticks(100)) ==
                        FuriStatusOk;
        if(received && event.kind == DeskEventExit) break;
        now = furi_get_tick();

        if(received && event.kind == DeskEventToggleView) {
            furi_mutex_acquire(app.model_mutex, FuriWaitForever);
            bool live = app.screen == DeskLive;
            if(live) {
                app.sample.large_clock = !app.sample.large_clock;
                FURI_CRITICAL_ENTER();
                app.pending_view = app.sample.large_clock ? DeskViewClock : DeskViewStats;
                FURI_CRITICAL_EXIT();
            }
            furi_mutex_release(app.model_mutex);
            if(live) view_port_update(view);
        } else if(received && event.kind == DeskEventViewSaved) {
            FURI_CRITICAL_ENTER();
            if(app.pending_view == event.saved_view) app.pending_view = DeskViewNone;
            FURI_CRITICAL_EXIT();
        } else if(received) {
            saw_frame = true;
            last_frame = now;
            furi_mutex_acquire(app.model_mutex, FuriWaitForever);
            bool turn_on = app.screen != DeskLive;
            app.sample = event.sample;
            if(app.pending_view != DeskViewNone) {
                app.sample.large_clock = app.pending_view == DeskViewClock;
            }
            app.clock_seconds = event.sample.clock_quarters / QUARTERS_PER_SECOND;
            app.screen = DeskLive;
            furi_mutex_release(app.model_mutex);
            if(turn_on) notification_message(notifications, &sequence_display_backlight_enforce_on);
            view_port_update(view);
        } else if(app.screen != DeskBlank && now - last_frame >= furi_ms_to_ticks(LINK_TIMEOUT_MS)) {
            furi_mutex_acquire(app.model_mutex, FuriWaitForever);
            app.screen = DeskBlank;
            furi_mutex_release(app.model_mutex);
            view_port_update(view);
            notification_message(notifications, &sequence_display_backlight_enforce_auto);
            notification_message(notifications, &sequence_display_backlight_off);
        } else if(app.screen == DeskLive && app.sample.show_seconds) {
            // Keep the clock ticking between samples and wrap at midnight.
            uint32_t clock_quarters = app.sample.clock_quarters + (now - last_frame) / quarter_ticks;
            uint32_t clock_seconds = clock_quarters / QUARTERS_PER_SECOND % SECONDS_PER_DAY;
            if(clock_seconds != app.clock_seconds) {
                furi_mutex_acquire(app.model_mutex, FuriWaitForever);
                app.clock_seconds = clock_seconds;
                furi_mutex_release(app.model_mutex);
                view_port_update(view);
            }
        }

        if(now - last_poll >= furi_ms_to_ticks(1000)) {
            last_poll = now;
            if(furi_hal_power_get_usb_voltage() >= 4.0f) {
                saw_power = true;
                last_power = now;
            } else if(saw_frame && saw_power && now - last_power >= furi_ms_to_ticks(POWER_TIMEOUT_MS)) {
                power_off_requested = true;
                break;
            }
        }
    }

    gui_remove_view_port(gui, view);
    view_port_free(view);
    // The loader invokes the callback on its own thread. Clear it before freeing the queue.
    FuriMessageQueue* events = app.events;
    FURI_CRITICAL_ENTER();
    furi_thread_set_signal_callback(thread, NULL, NULL);
    app.events = NULL;
    FURI_CRITICAL_EXIT();
    notification_message(notifications, &sequence_display_backlight_enforce_auto);
    furi_record_close(RECORD_NOTIFICATION);
    furi_record_close(RECORD_GUI);
    furi_mutex_free(app.model_mutex);
    furi_message_queue_free(events);

    if(power_off_requested && furi_hal_power_get_usb_voltage() < 4.0f) {
        Power* power = furi_record_open(RECORD_POWER);
        power_off(power);
        furi_record_close(RECORD_POWER);
    }
    return 0;
}
